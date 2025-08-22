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

DOCKER_COMPOSE_DOWNLOAD_URL := "https://github.com/docker/compose/releases/download/v$(DOCKER_COMPOSE_VERSION)/docker-compose-$(OS)-$(GOARCH)"


ifndef GOBIN
export GOBIN := $(MAKEFILE_PATH)/bin
endif

export PATH := $(GOBIN):$(PATH)

COMPOSE := docker-compose -f $(MAKEFILE_PATH)/test/docker-compose.yml

.PHONY: clean
clean:
	dotnet clean ScyllaDB.Alternator.csproj
	dotnet clean Test/ScyllaDB.Alternator.Test.csproj
	rm -rf bin/ obj/ Test/bin/ Test/obj/

.PHONY: build
build:
	dotnet build ScyllaDB.Alternator.csproj --configuration Release
	dotnet build Test/ScyllaDB.Alternator.Test.csproj --configuration Release

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

.PHONY: test-unit
test-unit:
	dotnet test Test/ScyllaDB.Alternator.Test.csproj --filter "Category=Unit" --logger:"console;verbosity=normal"

.PHONY: test-integration
test-integration: scylla-start
	dotnet test Test/ScyllaDB.Alternator.Test.csproj --filter "Category=Integration" --logger:"console;verbosity=normal"

.PHONY: .prepare-cert
.prepare-cert:
	@[ -f "${MAKEFILE_PATH}/Test/scylla/db.key" ] || (echo "Prepare certificate" && cd ${MAKEFILE_PATH}/Test/scylla/ && openssl req -subj "/C=US/ST=Denial/L=Springfield/O=Dis/CN=www.example.com" -x509 -newkey rsa:4096 -keyout db.key -out db.crt -days 3650 -nodes && chmod 644 db.key)

.PHONY: scylla-start
scylla-start: .prepare-cert $(GOBIN)/docker-compose
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
