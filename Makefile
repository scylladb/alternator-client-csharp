SHELL := bash
.ONESHELL:
.SHELLFLAGS := -eo pipefail -c

MAKEFILE_PATH ?= $(abspath $(dir $(abspath $(lastword $(MAKEFILE_LIST)))))
RELEASE_MAKEFILE ?= $(abspath $(firstword $(MAKEFILE_LIST)))
RELEASE_GUARD ?= $(dir $(RELEASE_MAKEFILE))scripts/release/guard.py
RELEASE_SIGNING_HELPER ?= $(dir $(RELEASE_MAKEFILE))scripts/release/signing.py

DOTNET_VERBOSITY := normal
DOTNET_FORMAT_VERBOSITY ?= diagnostic
ifdef IS_CICD
	DOTNET_VERBOSITY := minimal
endif


ifndef GOBIN
export GOBIN := $(MAKEFILE_PATH)/bin
endif

export PATH := $(GOBIN):$(PATH)

SCYLLA_CCM_COMMIT := d15a2fab9d22fffad8a30c806a7c8e1632e58aae
SCYLLA_CCM_VENV := $(GOBIN)/scylla-ccm-$(SCYLLA_CCM_COMMIT)
PINNED_SCYLLA_CCM_PATH := $(SCYLLA_CCM_VENV)/bin/ccm
SCYLLA_CCM_PATH ?= $(PINNED_SCYLLA_CCM_PATH)
SCYLLA_VERSION ?= release:2025.2
PACKAGE_OUTPUT_DIR ?= $(MAKEFILE_PATH)/nupkgs
NUGET_SOURCE ?= https://api.nuget.org/v3/index.json
RELEASE_SKIP_TESTS ?= false
RELEASE_VERSION ?=
export MAKEFILE_PATH RELEASE_MAKEFILE RELEASE_SIGNING_HELPER RELEASE_GUARD
export RELEASE_VERSION RELEASE_SKIP_TESTS PACKAGE_OUTPUT_DIR NUGET_SOURCE
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
verify: build check test-unit test-infrastructure try-get

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
check: check-license-headers check-dotnet-format

.PHONY: fix
fix: fix-dotnet-format

.PHONY: check-license-headers
check-license-headers:
	dotnet format analyzers --verify-no-changes --diagnostics SA1633 SA1636 SA1638 --severity warn --verbosity $(DOTNET_FORMAT_VERBOSITY) ScyllaDB.Alternator.csproj
	dotnet format analyzers --verify-no-changes --diagnostics SA1633 SA1636 SA1638 --severity warn --verbosity $(DOTNET_FORMAT_VERBOSITY) UnitTests/ScyllaDB.Alternator.Test.csproj
	dotnet format analyzers --verify-no-changes --diagnostics SA1633 SA1636 SA1638 --severity warn --verbosity $(DOTNET_FORMAT_VERBOSITY) IntegrationTests/ScyllaDB.Alternator.Test.csproj

.PHONY: check-dotnet-format
check-dotnet-format:
	dotnet format --verify-no-changes --severity warn --verbosity $(DOTNET_FORMAT_VERBOSITY) ScyllaDB.Alternator.csproj

.PHONY: fix-dotnet-format
fix-dotnet-format:
	dotnet format --severity warn --verbosity $(DOTNET_FORMAT_VERBOSITY) ScyllaDB.Alternator.csproj

.PHONY: test
test: build check test-unit test-infrastructure test-integration

.PHONY: test-all
test-all: test

.PHONY: release-prepare
release-prepare: release-check-user
	+$(MAKE) -f "$(RELEASE_MAKEFILE)" release-build
	+$(MAKE) -f "$(RELEASE_MAKEFILE)" release-sign

.PHONY: release-check-user
release-check-user:
	@python3 "$(RELEASE_GUARD)" check-user

.PHONY: release-build _release-build _release-clean-outputs
release-build: release-check-user
	+python3 "$(RELEASE_GUARD)" run $(MAKE) -f "$(RELEASE_MAKEFILE)" _release-build

_release-clean-outputs: release-check-user
	@: "$${PACKAGE_OUTPUT_DIR:?PACKAGE_OUTPUT_DIR must not be empty}"
	rm -f -- "$$PACKAGE_OUTPUT_DIR"/*.nupkg "$$PACKAGE_OUTPUT_DIR"/*.nupkg.asc "$$PACKAGE_OUTPUT_DIR/SHA256SUMS" "$$PACKAGE_OUTPUT_DIR/SHA256SUMS.asc"

_release-build: _release-clean-outputs
	@if [[ "$(RELEASE_SKIP_TESTS)" != "true" ]] && [[ "$(RELEASE_SKIP_TESTS)" != "1" ]]; then \
		$(MAKE) -f "$(RELEASE_MAKEFILE)" build check test-unit; \
	fi
	$(MAKE) -f "$(RELEASE_MAKEFILE)" try-get

.PHONY: release-sign
release-sign: release-check-user
	(cd "$(PACKAGE_OUTPUT_DIR)" && sha256sum -- *.nupkg > SHA256SUMS)
	python3 "$(RELEASE_SIGNING_HELPER)" exec python3 "$(RELEASE_SIGNING_HELPER)" sign-files "$(PACKAGE_OUTPUT_DIR)"/*.nupkg "$(PACKAGE_OUTPUT_DIR)/SHA256SUMS"

.PHONY: test-release-signing
test-release-signing:
	python3 -m unittest discover -s scripts/release -p 'test_*.py' -v

.PHONY: release-push
release-push: release-check-user
	@python3 "$(RELEASE_GUARD)" publish

.PHONY: release
release: release-check-user
	+$(MAKE) -f "$(RELEASE_MAKEFILE)" release-prepare
	+$(MAKE) -f "$(RELEASE_MAKEFILE)" release-push

.PHONY: release-dry-run-summary
release-dry-run-summary: release-check-user
	@echo "Dry-run: NuGet package was built but not pushed"
	ls -l $(PACKAGE_OUTPUT_DIR)

.PHONY: release-dry-run
release-dry-run: release-check-user
	+$(MAKE) -f "$(RELEASE_MAKEFILE)" release-prepare
	+$(MAKE) -f "$(RELEASE_MAKEFILE)" release-dry-run-summary

.PHONY: checkout-one-commit-before
checkout-one-commit-before: release-check-user
	@python3 "$(RELEASE_GUARD)" checkout-target

.PHONY: test-unit
test-unit:
	dotnet test UnitTests/ScyllaDB.Alternator.Test.csproj --filter "Category=Unit" --logger:"console;verbosity=$(DOTNET_VERBOSITY)" --logger trx --results-directory UnitTests/TestResults --verbosity $(DOTNET_VERBOSITY)

.PHONY: test-infrastructure
test-infrastructure:
	dotnet test IntegrationTests/ScyllaDB.Alternator.Test.csproj --filter "Category=InfrastructureUnit" --logger:"console;verbosity=$(DOTNET_VERBOSITY)" --logger trx --results-directory IntegrationTests/TestResults --verbosity $(DOTNET_VERBOSITY)

.PHONY: test-integration
test-integration: ccm-install
	mkdir -p "$(MAKEFILE_PATH)/IntegrationTests/TestResults"
	for stale_dir in "$(MAKEFILE_PATH)"/IntegrationTests/TestResults/ccm-runtime.*; do
		if [[ -d "$$stale_dir" ]] && [[ -f "$$stale_dir/OWNER_PID" ]]; then
			owner_pid=$$(cat "$$stale_dir/OWNER_PID")
			if ! kill -0 "$$owner_pid" 2>/dev/null; then
				"$(MAKEFILE_PATH)/IntegrationTests/cleanup-ccm.sh" "$$stale_dir" "$(SCYLLA_CCM_PATH)"
			fi
		fi
	done
	run_dir=$$(mktemp -d "$(MAKEFILE_PATH)/IntegrationTests/TestResults/ccm-runtime.XXXXXX")
	echo "$${BASHPID}" > "$$run_dir/OWNER_PID"
	cleanup() { "$(MAKEFILE_PATH)/IntegrationTests/cleanup-ccm.sh" "$$run_dir" "$(SCYLLA_CCM_PATH)"; }
	trap cleanup EXIT INT TERM
	SCYLLA_CCM_PATH="$(SCYLLA_CCM_PATH)" SCYLLA_CCM_RUN_DIR="$$run_dir" SCYLLA_VERSION="$(SCYLLA_VERSION)" dotnet test IntegrationTests/ScyllaDB.Alternator.Test.csproj --filter "Category=Integration" --logger:"console;verbosity=$(DOTNET_VERBOSITY)" --logger trx --results-directory IntegrationTests/TestResults --verbosity $(DOTNET_VERBOSITY)

.PHONY: ccm-install
ccm-install:
	@if [[ -x "$(SCYLLA_CCM_PATH)" ]]; then exit 0; fi
	@if [[ "$(SCYLLA_CCM_PATH)" != "$(PINNED_SCYLLA_CCM_PATH)" ]]; then \
		echo "SCYLLA_CCM_PATH is not executable: $(SCYLLA_CCM_PATH)"; \
		exit 1; \
	fi
	@if ! command -v uv >/dev/null 2>&1; then \
		echo "uv is required to install scylla-ccm: https://docs.astral.sh/uv/"; \
		exit 1; \
	fi
	mkdir -p "$(GOBIN)"
	if [[ ! -x "$(SCYLLA_CCM_VENV)/bin/python" ]]; then uv venv "$(SCYLLA_CCM_VENV)"; fi
	uv pip install --python "$(SCYLLA_CCM_VENV)/bin/python" "git+https://github.com/scylladb/scylla-ccm.git@$(SCYLLA_CCM_COMMIT)"
