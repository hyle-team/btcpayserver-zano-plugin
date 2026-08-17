using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace BTCPayServer.Plugins.Zano.RPC
{
    public class JsonRpcClient
    {
        private const int RawJsonPreviewMaxChars = 256;

        // Per-method timeouts: HttpClient's default of 100s is too long for a wedged
        // local daemon — a single bad call used to stall a poll for nearly two minutes.
        // Fast methods (status/health probes) should fail much sooner so the summary
        // updater and listener stay responsive; the wallet-history paging call gets a
        // longer budget because a large recent-tx page can legitimately take a few
        // seconds on a busy wallet.
        private static readonly TimeSpan FastMethodTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan SlowMethodTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan DefaultMethodTimeout = TimeSpan.FromSeconds(30);

        private static readonly HashSet<string> FastMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            "getinfo",
            "get_wallet_info",
            // Point lookup of a single tx — answers in milliseconds on a healthy
            // daemon. The default 30s budget let one wedged daemon consume up to
            // ~90s (with retries) per probed payment inside the wallet-scan lock.
            "get_tx_details"
        };

        private static readonly HashSet<string> SlowMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            "get_recent_txs_and_info2"
        };

        // Transient failures (network blips, daemon restarts) get up to two retries
        // with exponential backoff. Application errors (JsonRpcApiException) and
        // malformed responses bypass retry — they're not going to fix themselves.
        private const int MaxRetries = 2;
        private static readonly TimeSpan BaseRetryDelay = TimeSpan.FromMilliseconds(200);

        // Retry is allow-listed to methods that are safe to repeat. A response-read timeout
        // AFTER the daemon already applied a state-changing call (e.g. a future `transfer`)
        // must never be retried — that risks a double-spend. Idempotent reads/status and
        // deterministic integrated-address generation are safe to replay; anything not listed
        // here surfaces the transient error to the caller instead of being retried blindly.
        private static readonly HashSet<string> RetryableMethods = new(StringComparer.OrdinalIgnoreCase)
        {
            "getinfo",
            "get_wallet_info",
            "get_recent_txs_and_info2",
            "get_tx_details",
            "get_asset_info",
            "make_integrated_address",
            "open_wallet"
        };

        private readonly Uri _address;
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public JsonRpcClient(Uri address, HttpClient client = null, ILogger<JsonRpcClient> logger = null)
        {
            _address = address;
            _httpClient = client ?? new HttpClient();
            _logger = logger ?? NullLogger<JsonRpcClient>.Instance;
        }

        private static TimeSpan TimeoutFor(string method) =>
            FastMethods.Contains(method) ? FastMethodTimeout
            : SlowMethods.Contains(method) ? SlowMethodTimeout
            : DefaultMethodTimeout;

        public async Task<TResponse> SendCommandAsync<TRequest, TResponse>(string method, TRequest data,
            CancellationToken cts = default)
        {
            var timeout = TimeoutFor(method);
            for (int attempt = 0; ; attempt++)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts);
                timeoutCts.CancelAfter(timeout);
                try
                {
                    return await SendCommandOnceAsync<TRequest, TResponse>(method, data, timeoutCts.Token);
                }
                catch (JsonRpcApiException)
                {
                    // Application-level error — the daemon answered, we just didn't
                    // like the answer. Retrying won't help.
                    throw;
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    // Caller cancellation propagates immediately.
                    throw;
                }
                catch (Exception ex) when (!cts.IsCancellationRequested && IsTransient(ex) && attempt < MaxRetries && RetryableMethods.Contains(method))
                {
                    var delay = TimeSpan.FromMilliseconds(BaseRetryDelay.TotalMilliseconds * (1L << attempt));
                    _logger.LogWarning(
                        "Transient JSON-RPC failure on {Endpoint} method={Method} attempt={Attempt}/{MaxRetries}, retrying in {DelayMs}ms: {Error}",
                        _address, method, attempt + 1, MaxRetries, delay.TotalMilliseconds, ex.GetType().Name);
                    await Task.Delay(delay, cts);
                }
            }
        }

        private async Task<TResponse> SendCommandOnceAsync<TRequest, TResponse>(string method, TRequest data,
            CancellationToken cts)
        {
            var jsonSerializer = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver()
            };
            var httpRequest = new HttpRequestMessage()
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri(_address, "json_rpc"),
                Content = new StringContent(
                    JsonConvert.SerializeObject(new JsonRpcCommand<TRequest>(method, data), jsonSerializer),
                    Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Accept.Clear();
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage rawResult = await _httpClient.SendAsync(httpRequest, cts);
            rawResult.EnsureSuccessStatusCode();
            var rawJson = await rawResult.Content.ReadAsStringAsync(cts);

            JsonRpcResult<TResponse> response;
            try
            {
                response = JsonConvert.DeserializeObject<JsonRpcResult<TResponse>>(rawJson, jsonSerializer);
            }
            catch (JsonException e)
            {
                // Log a truncated, structured preview only — wallet RPC bodies can contain
                // payment ids, tx hashes, amounts, asset ids, and history fragments, so we
                // never dump the full payload to stdout. Malformed JSON is not retried.
                var preview = rawJson is null
                    ? "(null)"
                    : (rawJson.Length > RawJsonPreviewMaxChars
                        ? rawJson.Substring(0, RawJsonPreviewMaxChars) + "…"
                        : rawJson);
                _logger.LogError(e,
                    "Failed to deserialize JSON-RPC response from {Endpoint} method={Method} previewLen={Length} preview={Preview}",
                    _address, method, rawJson?.Length ?? 0, preview);
                throw;
            }

            if (response.Error != null)
            {
                throw new JsonRpcApiException()
                {
                    Error = response.Error
                };
            }

            return response.Result;
        }

        // Transient = network/connectivity issue or per-call timeout expiry. Caller
        // cancellation is filtered out at the call site so it never reaches here.
        private static bool IsTransient(Exception ex) =>
            ex is HttpRequestException
            || ex is TaskCanceledException
            || ex is OperationCanceledException;

        public class NoRequestModel
        {
            public static readonly NoRequestModel Instance = new();
        }

        public class JsonRpcApiException : Exception
        {
            public JsonRpcResultError Error { get; set; }

            public override string Message => Error?.Message;
        }

        public class JsonRpcResultError
        {
            [JsonProperty("code")] public int Code { get; set; }
            [JsonProperty("message")] public string Message { get; set; }
            [JsonProperty("data")] dynamic Data { get; set; }
        }
        internal class JsonRpcResult<T>
        {
            [JsonProperty("result")] public T Result { get; set; }
            [JsonProperty("error")] public JsonRpcResultError Error { get; set; }
            [JsonProperty("id")] public string Id { get; set; }
        }

        internal class JsonRpcCommand<T>
        {
            [JsonProperty("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
            [JsonProperty("id")] public string Id { get; set; } = Guid.NewGuid().ToString();
            [JsonProperty("method")] public string Method { get; set; }

            [JsonProperty("params")] public T Parameters { get; set; }

            public JsonRpcCommand()
            {
            }

            public JsonRpcCommand(string method, T parameters)
            {
                Method = method;
                Parameters = parameters;
            }
        }
    }
}
