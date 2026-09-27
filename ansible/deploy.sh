#!/usr/bin/env bash
set -euo pipefail

mode=deploy
if [[ "${1:-}" == "--preflight" ]]; then
  mode=preflight
  shift
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
inventory_path="$script_dir/inventory.yml"

if [[ ! -f "$inventory_path" ]]; then
  echo "Required inventory not found: $inventory_path" >&2
  exit 2
fi

if ! command -v python3 >/dev/null 2>&1; then
  echo "Python 3 is required inside WSL/Linux." >&2
  echo "Ubuntu: sudo apt-get update && sudo apt-get install -y python3 python3-venv" >&2
  exit 2
fi

if ! python3 -m venv "$script_dir/.runtime"; then
  echo "Could not create the Ansible runtime." >&2
  echo "Ubuntu: sudo apt-get install -y python3-venv" >&2
  exit 2
fi
# shellcheck disable=SC1091
source "$script_dir/.runtime/bin/activate"
python -m pip install --disable-pip-version-check -r "$script_dir/requirements.txt"
ansible-galaxy collection install -r "$script_dir/collections/requirements.yml" \
  -p "$script_dir/.collections"
cd "$script_dir"

if [[ "$mode" == "preflight" ]]; then
  echo "Inventory graph"
  ansible-inventory --graph
  echo "Testing Ubuntu packet hosts over SSH"
  ansible linux_agents -m ansible.builtin.ping "$@"
  echo "Testing the Red Team Windows controller over WinRM"
  ansible commando_servers -m ansible.windows.win_ping "$@"
  echo "Testing Windows packet hosts over WinRM"
  ansible windows_agents -m ansible.windows.win_ping "$@"
  exit 0
fi

ansible-playbook site.yml "$@"

