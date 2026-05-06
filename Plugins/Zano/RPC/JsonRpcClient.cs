using System;
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

        private readonly Uri _address;
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public JsonRpcClient(Uri address, HttpClient client = null, ILogger<JsonRpcClient> logger = null)
        {
            _address = address;
            _httpClient = client ?? new HttpClient();
            _logger = logger ?? NullLogger<JsonRpcClient>.Instance;
        }


        public async Task<TResponse> SendCommandAsync<TRequest, TResponse>(string method, TRequest data,
            CancellationToken cts = default)
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
            var rawJson = await rawResult.Content.ReadAsStringAsync();

            JsonRpcResult<TResponse> response;
            try
            {
                response = JsonConvert.DeserializeObject<JsonRpcResult<TResponse>>(rawJson, jsonSerializer);
            }
            catch (Exception e)
            {
                // Log a truncated, structured preview only — wallet RPC bodies can contain
                // payment ids, tx hashes, amounts, asset ids, and history fragments, so we
                // never dump the full payload to stdout.
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
