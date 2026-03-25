FROM ubuntu:22.04 AS builder

RUN apt-get update && apt-get install -y \
    build-essential cmake git wget \
    libboost-all-dev libssl-dev libunwind-dev \
    pkg-config && \
    rm -rf /var/lib/apt/lists/*

ARG ZANO_BRANCH=master
RUN git clone --depth 1 --branch ${ZANO_BRANCH} --recursive \
    https://github.com/hyle-team/zano.git /zano

WORKDIR /zano/build
RUN cmake .. -DCMAKE_BUILD_TYPE=Release && \
    make -j$(nproc) daemon simplewallet

FROM ubuntu:22.04

RUN apt-get update && apt-get install -y \
    libboost-filesystem1.74.0 libboost-thread1.74.0 \
    libboost-program-options1.74.0 libboost-regex1.74.0 \
    libboost-serialization1.74.0 libboost-system1.74.0 \
    libboost-date-time1.74.0 libboost-chrono1.74.0 \
    libboost-locale1.74.0 \
    libssl3 libunwind8 curl && \
    rm -rf /var/lib/apt/lists/*

COPY --from=builder /zano/build/src/zanod /usr/local/bin/zanod
COPY --from=builder /zano/build/src/simplewallet /usr/local/bin/simplewallet

RUN mkdir -p /data /wallet

VOLUME ["/data", "/wallet"]

EXPOSE 11211 11212

ENTRYPOINT ["zanod"]
CMD ["--rpc-bind-ip=0.0.0.0", "--rpc-bind-port=11211", "--data-dir=/data"]
