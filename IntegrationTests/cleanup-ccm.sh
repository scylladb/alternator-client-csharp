#!/usr/bin/env bash

# Copyright ScyllaDB, Inc.
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
# http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.

set -uo pipefail

run_dir=${1:?CCM run directory is required}
ccm=${2:?CCM executable is required}

case "$run_dir" in
    */IntegrationTests/TestResults/ccm-runtime.*) ;;
    /tmp/alternator-client-csharp-ccm-*) ;;
    *)
        echo "Refusing to clean unexpected CCM run directory: $run_dir" >&2
        exit 1
        ;;
esac

clusters_dir="$run_dir/clusters"
[[ -d "$clusters_dir" ]] || exit 0

for config_dir in "$clusters_dir"/*; do
    [[ -d "$config_dir" ]] || continue
    [[ -f "$config_dir/CURRENT" ]] || continue
    cluster_name=$(tr -d '\r\n' < "$config_dir/CURRENT")
    [[ -n "$cluster_name" ]] || continue

    diagnostics="$run_dir/diagnostics/$cluster_name-emergency"
    mkdir -p "$diagnostics"
    [[ -f "$config_dir/ccm-commands.log" ]] && cp "$config_dir/ccm-commands.log" "$diagnostics/"
    for logs_dir in "$config_dir/$cluster_name"/node*/logs; do
        [[ -d "$logs_dir" ]] || continue
        node_name=$(basename "$(dirname "$logs_dir")")
        mkdir -p "$diagnostics/$node_name"
        cp -a "$logs_dir" "$diagnostics/$node_name/"
    done

    "$ccm" remove --config-dir "$config_dir" "$cluster_name" || {
        "$ccm" stop --config-dir "$config_dir" || true
        "$ccm" remove --config-dir "$config_dir" "$cluster_name" || true
    }
done
