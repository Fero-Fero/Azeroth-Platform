FROM ubuntu:22.04

ENV DEBIAN_FRONTEND=noninteractive

ARG MODCHECK_DOCKERFILE_SHA=unknown
LABEL azeroth-platform.modcheck=1
LABEL azeroth-platform.modcheck.dockerfile-sha=$MODCHECK_DOCKERFILE_SHA

# Toolchain matching AzerothCore 3.3.5a Docker builder images (cmake + ninja + clang),
# plus curl/json headers that Ollama Buddy modules need (vanilla AC Dockerfiles omit them).
RUN apt-get update && apt-get install -y --no-install-recommends \
        ca-certificates \
        clang \
        cmake \
        default-libmysqlclient-dev \
        git \
        libboost-all-dev \
        libbz2-dev \
        libcurl4-openssl-dev \
        libncurses-dev \
        libreadline-dev \
        libssl-dev \
        make \
        ninja-build \
        nlohmann-json3-dev \
        pkg-config \
        zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src

# CMake/ninja call git for the core revision. The stacked data volume is owned by
# another uid; mark the workdir as safe at runtime too (see ModuleCheckCompiler).
RUN git config --system --add safe.directory '*' || true

CMD ["cmake", "--version"]
