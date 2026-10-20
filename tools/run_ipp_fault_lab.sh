#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(CDPATH= cd -- "$script_dir/.." && pwd)
mode=${1:-run}

case "$mode" in
    run)
        container_command='python3 tools/ipp_fault_lab.py run --scenario all'
        ;;
    test)
        container_command='python3 -m unittest discover -s tests -p test_ipp_fault_lab.py'
        ;;
    *)
        printf '%s\n' 'usage: tools/run_ipp_fault_lab.sh [run|test]' >&2
        exit 2
        ;;
esac

image=paperbatch-ipp-lab:local
docker build --pull --platform linux/amd64 \
    --file "$repo_root/tools/ipp-lab/Dockerfile" \
    --tag "$image" \
    "$repo_root"

docker run --rm --platform linux/amd64 \
    --network none \
    --read-only \
    --tmpfs /tmp:rw,noexec,nosuid,nodev,size=32m \
    --cap-drop=ALL \
    --security-opt=no-new-privileges \
    --pids-limit=64 \
    --memory=512m \
    --cpus=1 \
    "$image" \
    sh -c "$container_command"
