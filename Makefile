SHELL := bash
.ONESHELL:
.SHELLFLAGS := -eo pipefail -c

define dl_tgz
	@if ! $(1) 2>/dev/null 1>&2; then \
		[ -d "$(GOBIN)" ] || mkdir "$(GOBIN)"; \
		if [ ! -f "$(GOBIN)/$(1)" ]; then \
			echo "Downloading $(GOBIN)/$(1)"; \
			curl --progress-bar -L $(2) | tar zxf - --wildcards --strip 1 -C $(GOBIN) '*/$(1)'; \
			chmod +x "$(GOBIN)/$(1)"; \
		fi; \
	fi
endef

define dl_bin
	@if ! $(1) 2>/dev/null 1>&2; then \
		[ -d "$(GOBIN)" ] || mkdir "$(GOBIN)"; \
		if [ ! -f "$(GOBIN)/$(1)" ]; then \
			echo "Downloading $(GOBIN)/$(1)"; \
			curl --progress-bar -L $(2) --output "$(GOBIN)/$(1)"; \
			chmod +x "$(GOBIN)/$(1)"; \
		fi; \
	fi
endef

DOCKER_COMPOSE_VERSION := 2.34.0

MAKEFILE_PATH := $(abspath $(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
ARCH := $(shell uname -m)
OS := $(shell uname -s | tr A-Z a-z)

ifeq ($(ARCH),aarch64)
	DOCKER_COMPOSE_DOWNLOAD_URL := "https://github.com/docker/compose/releases/download/v$(DOCKER_COMPOSE_VERSION)/docker-compose-$(OS)-aarch64"
else ifeq ($(ARCH),x86_64)
	DOCKER_COMPOSE_DOWNLOAD_URL := "https://github.com/docker/compose/releases/download/v$(DOCKER_COMPOSE_VERSION)/docker-compose-$(OS)-x86_64"
else
	$(error Unknown architecture "$(ARCH)")
endif

DOTNET_VERBOSITY := normal
ifdef IS_CICD
	DOTNET_VERBOSITY := minimal
endif


ifndef GOBIN
export GOBIN := $(MAKEFILE_PATH)/bin
endif

export PATH := $(GOBIN):$(PATH)

COMPOSE := docker-compose -f $(MAKEFILE_PATH)/IntegrationTests/docker-compose.yml
ALTERNATOR_ENDPOINT ?= http://172.45.0.2:9998
ALTERNATOR_HTTPS_ENDPOINT ?= https://172.45.0.2:9999
ALTERNATOR_CA_CERT_PATH ?= $(CERT_DIR)/db.crt
SCYLLA_IMAGE := scylladb/scylla:2025.2
DOCKER_CACHE_DIR := $(MAKEFILE_PATH)/.docker-cache
DOCKER_CACHE_FILE := $(DOCKER_CACHE_DIR)/scylla-image.tar
CERT_CACHE_DIR := $(MAKEFILE_PATH)/.cert-cache
CERT_DIR := $(MAKEFILE_PATH)/IntegrationTests/scylla
PACKAGE_OUTPUT_DIR ?= $(MAKEFILE_PATH)/nupkgs
NUGET_SOURCE ?= https://api.nuget.org/v3/index.json
NUGET_API_KEY ?=
RELEASE_SKIP_TESTS ?= false
RELEASE_VERSION ?=
PACK_VERSION_ARGS :=
ifneq ($(strip $(RELEASE_VERSION)),)
	PACK_VERSION_ARGS := /p:Version=$(RELEASE_VERSION) /p:PackageVersion=$(RELEASE_VERSION)
endif

.PHONY: clean
clean:
	dotnet clean ScyllaDB.Alternator.csproj
	dotnet clean UnitTests/ScyllaDB.Alternator.Test.csproj
	dotnet clean IntegrationTests/ScyllaDB.Alternator.Test.csproj
	rm -rf bin/ obj/ UnitTests/bin/ UnitTests/obj/ IntegrationTests/bin/ IntegrationTests/obj/ $(PACKAGE_OUTPUT_DIR)/

.PHONY: build
build:
	dotnet build ScyllaDB.Alternator.csproj --configuration Release --verbosity $(DOTNET_VERBOSITY)
	dotnet build UnitTests/ScyllaDB.Alternator.Test.csproj --configuration Release --verbosity $(DOTNET_VERBOSITY)
	dotnet build IntegrationTests/ScyllaDB.Alternator.Test.csproj --configuration Release --verbosity $(DOTNET_VERBOSITY)

.PHONY: verify
verify: build check test-unit try-get

.PHONY: lint
lint: check

.PHONY: lint-fix
lint-fix: fix

.PHONY: compile
compile:
	dotnet build ScyllaDB.Alternator.csproj --configuration Release --verbosity $(DOTNET_VERBOSITY)

.PHONY: compile-test
compile-test:
	dotnet build UnitTests/ScyllaDB.Alternator.Test.csproj --configuration Release --verbosity $(DOTNET_VERBOSITY)
	dotnet build IntegrationTests/ScyllaDB.Alternator.Test.csproj --configuration Release --verbosity $(DOTNET_VERBOSITY)

.PHONY: compile-demo
compile-demo: compile-test

.PHONY: pack
pack:
	mkdir -p $(PACKAGE_OUTPUT_DIR)
	dotnet pack ScyllaDB.Alternator.csproj --configuration Release --output $(PACKAGE_OUTPUT_DIR) --verbosity $(DOTNET_VERBOSITY) $(PACK_VERSION_ARGS)

.PHONY: try-get
try-get: pack
	tmpdir=$$(mktemp -d)
	trap 'rm -rf "$$tmpdir"' EXIT
	local_source=$$(cd "$(PACKAGE_OUTPUT_DIR)" && pwd)
	package_path=$$(ls -t "$$local_source"/ScyllaDB.Alternator.*.nupkg | head -n 1)
	if [[ "$$package_path" == "" ]]; then
		echo "No ScyllaDB.Alternator package found in $$local_source"
		exit 1
	fi
	package_file=$$(basename "$$package_path")
	package_version=$${package_file#ScyllaDB.Alternator.}
	package_version=$${package_version%.nupkg}
	printf '%s\n' \
		'<?xml version="1.0" encoding="utf-8"?>' \
		'<configuration>' \
		'  <packageSources>' \
		'    <clear />' \
		"    <add key=\"local\" value=\"$$local_source\" />" \
		"    <add key=\"nuget.org\" value=\"$(NUGET_SOURCE)\" />" \
		'  </packageSources>' \
		'  <packageSourceMapping>' \
		'    <packageSource key="local">' \
		'      <package pattern="ScyllaDB.Alternator" />' \
		'    </packageSource>' \
		'    <packageSource key="nuget.org">' \
		'      <package pattern="*" />' \
		'    </packageSource>' \
		'  </packageSourceMapping>' \
		'</configuration>' \
		> "$$tmpdir/NuGet.config"
	printf '%s\n' \
		'<Project Sdk="Microsoft.NET.Sdk">' \
		'  <PropertyGroup>' \
		'    <OutputType>Exe</OutputType>' \
		'    <TargetFramework>net8.0</TargetFramework>' \
		'    <ImplicitUsings>enable</ImplicitUsings>' \
		'    <Nullable>enable</Nullable>' \
		'    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>' \
		'  </PropertyGroup>' \
		'</Project>' \
		> "$$tmpdir/TryGet.csproj"
	NUGET_PACKAGES="$$tmpdir/packages" dotnet add "$$tmpdir/TryGet.csproj" package ScyllaDB.Alternator --version "$$package_version"
	printf '%s\n' \
		'using System.Reflection;' \
		'using Amazon.DynamoDBv2;' \
		'using ScyllaDB.Alternator;' \
		'using ScyllaDB.Alternator.Routing;' \
		'' \
		'using var client = AlternatorDynamoDBClient.builder()' \
		'    .endpointOverride("http://127.0.0.1:8000")' \
		'    .region("us-east-1")' \
		'    .withRoutingScope(DatacenterScope.of("dc1", ClusterScope.create()))' \
		'    .WithoutValidation()' \
		'    .WithDeferredStart()' \
		'    .build();' \
		'' \
		'if (client is not AmazonDynamoDBClient)' \
		'{' \
		'    throw new InvalidOperationException("builder().build() did not return AmazonDynamoDBClient.");' \
		'}' \
		'' \
		'var config = AlternatorConfig.builder()' \
		'    .withSeedNode("http://127.0.0.1:8000")' \
		'    .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)' \
		'    .withOptimizeHeaders(true)' \
		'    .build();' \
		'' \
		'if (!config.getRequiredHeaders().Contains("Content-Encoding"))' \
		'{' \
		'    throw new InvalidOperationException("Packaged API did not expose compression headers.");' \
		'}' \
		'' \
		'var assembly = typeof(AlternatorDynamoDBClient).Assembly;' \
		'var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;' \
		'if (string.IsNullOrWhiteSpace(informationalVersion) || informationalVersion == "0.0.0.0")' \
		'{' \
		'    throw new InvalidOperationException("Packaged assembly is missing informational version metadata.");' \
		'}' \
		'' \
		'Console.WriteLine("Installed ScyllaDB.Alternator " + informationalVersion + " from NuGet package.");' \
		> "$$tmpdir/Program.cs"
	NUGET_PACKAGES="$$tmpdir/packages" dotnet run --project "$$tmpdir/TryGet.csproj" --no-restore --verbosity $(DOTNET_VERBOSITY)

.PHONY: clean-caches
clean-caches:
	rm -rf ~/.nuget/packages/*

.PHONY: check
check: check-dotnet-format

.PHONY: fix
fix: fix-dotnet-format

.PHONY: check-dotnet-format
check-dotnet-format:
	dotnet format --verify-no-changes --severity warn --verbosity diagnostic ScyllaDB.Alternator.csproj

.PHONY: fix-dotnet-format
fix-dotnet-format:
	dotnet format --severity warn --verbosity diagnostic ScyllaDB.Alternator.csproj

.PHONY: test
test: build check test-unit test-integration

.PHONY: test-all
test-all: test

.PHONY: release-prepare
release-prepare:
	@if [[ "$(RELEASE_SKIP_TESTS)" != "true" ]] && [[ "$(RELEASE_SKIP_TESTS)" != "1" ]]; then \
		$(MAKE) build check test-unit; \
	fi
	$(MAKE) try-get

.PHONY: release-push
release-push:
	@if [[ "$(NUGET_API_KEY)" == "" ]]; then \
		echo "NUGET_API_KEY is empty, can't continue"; \
		exit 1; \
	fi
	dotnet nuget push $(PACKAGE_OUTPUT_DIR)/*.nupkg --api-key "$(NUGET_API_KEY)" --source "$(NUGET_SOURCE)" --skip-duplicate

.PHONY: release
release: release-prepare release-push

.PHONY: release-dry-run-summary
release-dry-run-summary:
	@echo "Dry-run: NuGet package was built but not pushed"
	ls -l $(PACKAGE_OUTPUT_DIR)

.PHONY: release-dry-run
release-dry-run: release-prepare release-dry-run-summary

.PHONY: checkout-one-commit-before
checkout-one-commit-before:
	@if [[ "$(RELEASE_TARGET_TAG)" != "" ]] && [[ "$(RELEASE_TARGET_TAG)" != "main" ]]; then \
		git fetch --tags --prune --unshallow || git fetch --tags --prune || true; \
		if git rev-parse -q --verify "refs/tags/$(RELEASE_TARGET_TAG)" >/dev/null; then \
			echo "Checking out one commit before $(RELEASE_TARGET_TAG)"; \
			cp -f Makefile /tmp/tmp-Makefile; \
			git checkout "$(RELEASE_TARGET_TAG)~1"; \
			git tag -d "$(RELEASE_TARGET_TAG)" || true; \
			mv -f /tmp/tmp-Makefile ./Makefile; \
		else \
			echo "$(RELEASE_TARGET_TAG) is not a tag; leaving checkout unchanged"; \
		fi; \
	fi

.PHONY: test-unit
test-unit:
	dotnet test UnitTests/ScyllaDB.Alternator.Test.csproj --filter "Category=Unit" --logger:"console;verbosity=$(DOTNET_VERBOSITY)" --logger trx --results-directory UnitTests/TestResults --verbosity $(DOTNET_VERBOSITY)

.PHONY: test-integration
test-integration: scylla-start wait-for-alternator
	ALTERNATOR_ENDPOINT=$(ALTERNATOR_ENDPOINT) ALTERNATOR_HTTPS_ENDPOINT=$(ALTERNATOR_HTTPS_ENDPOINT) ALTERNATOR_CA_CERT_PATH=$(ALTERNATOR_CA_CERT_PATH) dotnet test IntegrationTests/ScyllaDB.Alternator.Test.csproj --filter "Category=Integration" --logger:"console;verbosity=$(DOTNET_VERBOSITY)" --logger trx --results-directory IntegrationTests/TestResults --verbosity $(DOTNET_VERBOSITY) || ($(MAKE) scylla-stop && exit 1)
	$(MAKE) scylla-stop

.PHONY: wait-for-alternator
wait-for-alternator:
	@echo "Waiting for Alternator to be ready..."
	@for i in $$(seq 1 60); do \
		if curl -sf $(ALTERNATOR_ENDPOINT)/ >/dev/null 2>&1; then \
			echo "Alternator is ready (waited $${i}s)"; \
			break; \
		fi; \
		if [ $$i -eq 60 ]; then \
			echo "Timed out waiting for Alternator"; \
			exit 1; \
		fi; \
		sleep 1; \
	done

.PHONY: .prepare-cert
.prepare-cert:
	@if [ ! -f "$(CERT_DIR)/db.key" ] || [ ! -f "$(CERT_DIR)/db.crt" ] || ! openssl x509 -in "$(CERT_DIR)/db.crt" -noout -text | grep "IP Address:172.45.0.2" >/dev/null; then \
		echo "Prepare certificate"; \
		cd "$(CERT_DIR)"; \
		rm -f db.key db.crt; \
		openssl req -subj "/C=US/ST=Denial/L=Springfield/O=Dis/CN=www.example.com" -x509 -newkey rsa:4096 -keyout db.key -out db.crt -days 3650 -nodes -addext "subjectAltName=IP:172.45.0.2,IP:172.45.0.3,IP:172.45.0.4"; \
		chmod 644 db.key; \
	fi

.PHONY: scylla-start
scylla-start: cert-cache-load $(GOBIN)/docker-compose docker-cache-load
	@sudo sysctl -w fs.aio-max-nr=10485760
	$(COMPOSE) up -d

.PHONY: scylla-stop
scylla-stop: $(GOBIN)/docker-compose
	$(COMPOSE) down

.PHONY: scylla-kill
scylla-kill: $(GOBIN)/docker-compose
	$(COMPOSE) kill

.PHONY: scylla-rm
scylla-rm: $(GOBIN)/docker-compose
	$(COMPOSE) rm -f

$(GOBIN)/docker-compose: Makefile
	$(call dl_bin,docker-compose,$(DOCKER_COMPOSE_DOWNLOAD_URL))

.PHONY: docker-pull
docker-pull:
	docker pull $(SCYLLA_IMAGE)

.PHONY: docker-cache-save
docker-cache-save: docker-pull
	@mkdir -p $(DOCKER_CACHE_DIR)
	docker save $(SCYLLA_IMAGE) -o $(DOCKER_CACHE_FILE)

.PHONY: docker-cache-load
docker-cache-load:
	@if [ -f "$(DOCKER_CACHE_FILE)" ]; then \
		echo "Loading Docker image from cache..."; \
		docker load -i $(DOCKER_CACHE_FILE); \
	else \
		echo "Cache file not found, pulling image..."; \
		$(MAKE) docker-pull; \
	fi

.PHONY: cert-cache-save
cert-cache-save: .prepare-cert
	@mkdir -p $(CERT_CACHE_DIR)
	cp $(CERT_DIR)/db.key $(CERT_DIR)/db.crt $(CERT_CACHE_DIR)/

.PHONY: cert-cache-load
cert-cache-load:
	@if [ -f "$(CERT_CACHE_DIR)/db.key" ] && [ -f "$(CERT_CACHE_DIR)/db.crt" ] && openssl x509 -in "$(CERT_CACHE_DIR)/db.crt" -noout -text | grep "IP Address:172.45.0.2" >/dev/null; then \
		echo "Loading certificates from cache..."; \
		cp $(CERT_CACHE_DIR)/db.key $(CERT_CACHE_DIR)/db.crt $(CERT_DIR)/; \
		chmod 644 $(CERT_DIR)/db.key; \
	else \
		echo "Certificate cache not found or missing SANs, generating..."; \
		$(MAKE) .prepare-cert; \
	fi
