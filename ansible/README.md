# Commando Ansible deployment

This is a self-contained deployment bundle. After Ansible has network access to
the managed hosts, the only deployment-specific input is one inventory file.

The bundle includes:

- self-contained Windows server and Windows/Linux agent executables;
- the dashboard and server static assets;
- roles, templates, policy generation, and dependency manifests;
- automatic operator/enrollment secret generation;
- automatic private-CA and server-certificate generation;
- CA installation on Windows and Ubuntu agents;
- Windows boot tasks and a Linux `systemd` unit;
- firewall, permissions, enrollment, and health checks.

Windows processes use restartable boot Scheduled Tasks because the current
binaries are console applications rather than native Windows Services.

## Inventory

Edit the included `inventory.yml`. It is the only deployment file that needs to
be configured. The inventory must provide:

- exactly one host in `commando_servers`;
- each authorized agent in `linux_agents` or `windows_agents`;
- `commando_event_starts_utc` and `commando_event_expires_utc`;
- each agent's exact `commando_agent_name`, connection address, hash roots, and
  locally approved service definitions;
- normal Ansible SSH/WinRM connection variables.

Use SSH keys, Kerberos, the launcher's `-AskPass` switches, or encrypted Ansible
Vault values. Do not commit plain-text connection passwords into the inventory.

`commando_agent_hostname` must equal the machine's actual hostname and defaults
to `commando_agent_name`. `commando_host_address` may be set separately when an
Ansible connection address differs from the event-policy address.

The playbook derives the following without additional configuration files:

- HTTPS server URL and certificate SANs from the server inventory host;
- exact `/32` enrollment networks and allowed targets from agent addresses;
- the event policy and its service IDs;
- unique per-host enrollment keys.

Keep every `allow_pause` value false until the corresponding service recovery has
been rehearsed. Unrestricted PowerShell remains disabled unless a Windows host's
inventory explicitly sets `commando_allow_unrestricted_powershell: true`.

## Deploy with only `inventory.yml`

The Bravo infrastructure already provides the preferred split:

- `linux-server-1` (`10.110.30.11`) runs Ansible during installation;
- `win-server-1` (`10.110.30.21`) runs the native Windows Commando server and
  dashboard.

Connect to `linux-server-1` through its documented Tailscale address, copy or
clone this repository there, and run:

```bash
bash ansible/deploy.sh
```

The Linux VM needs `python3` and `python3-venv` plus access to PyPI and Ansible
Galaxy on the first run. The launcher creates an isolated runtime, installs the
included requirements, and runs `site.yml` with the adjacent `inventory.yml`.

The Windows/WSL launcher remains available as a fallback when deploying from a
different Red Team Windows workstation:

```powershell
.\Deploy-Commando.ps1 -AskPass -AskBecomePass
```

If dependencies are already installed, run the playbook directly:

```bash
cd ansible
ansible-playbook site.yml
```

The first run creates `.generated/<commando_deployment_id>/` with random secrets,
a private CA, and the Kestrel PFX. Later runs reuse those files. Back up that
directory securely; losing it requires a new deployment identity and agent state.
Do not copy one deployment's generated directory into a different competition.
The dashboard operator key is stored in that directory as `operator-key`.

The deployment uses the bundled executables, so target machines do not need the
.NET SDK or access to NuGet.

## Security behavior

- Windows startup tasks run as `SYSTEM`. The Linux agent defaults to `root`
  because service pause/resume requires local service-manager privileges.
- Generated controller secrets and CA private keys use owner-only permissions.
- Agent configuration, enrollment state, transcripts, and server data are
  protected from ordinary users.
- The master enrollment key is installed only on the server; agents receive a
  host-specific derived key.
- Exact agent addresses become the default command targets and `/32` enrollment
  networks. Broader CIDRs require an explicit inventory override.
- Run only against competition hosts whose owners authorized installation.

## Complete usage guide

### 1. Choose the machines

Use `linux-server-1` as the installation controller and `win-server-1` as the
native Windows application server. Keep `linux-server-2` and `win-server-2` as
team infrastructure rather than silently treating them as scored agents. Do not
install the server on Coruscant or another scored host.

Copy the whole repository to `linux-server-1`. Do not copy only `site.yml` and
`inventory.yml`: the roles and bundled executables are also required. Managed
hosts do not need Ansible, the repository, or the .NET SDK.

### 2. Prepare host connectivity

- Connect to `linux-server-1` as `cyberrange` using the documented Tailscale IP
  and `bravo-key`.
- Configure SSH on Ubuntu agents and confirm the packet user can use `sudo`.
- Verify WinRM is enabled on `win-server-1` and the Windows packet hosts.
- Permit `linux-server-1` to reach SSH/WinRM on every managed host.
- Permit every agent subnet to reach `win-server-1` on TCP 443.
- Preserve the agent source IP when `commando_require_source_ip` is `true`.

SSH and WinRM are initial installation channels only. Blocking them after the
agent is installed does not stop normal operation: each agent initiates outbound
HTTPS polling to `10.110.30.21`. If Blue Team blocks deployment before
installation, Grey Team must pre-stage the agent or restore an authorized
management path. The tool does not bypass a blocked management channel.

Use the environment's approved SSH and WinRM configuration. The inventory uses
WinRM with NTLM and ignores the WinRM management certificate; Commando application
traffic is separately protected by the private CA generated during deployment.

### 3. Edit `inventory.yml`

`inventory.yml` is the only deployment-specific file. The packet's ten hosts,
addresses, operating systems, and scored services are already populated. Before
running, edit the short block at the top:

1. Confirm `10.110.30.21` is still assigned to `win-server-1`.
2. Confirm the Red controller administrator and the Windows/Linux packet users.
3. Confirm the official UTC event start and end.
4. Verify that MSSQL uses `MSSQLSERVER`, FTP uses `vsftpd`, and SMTP uses
   `postfix`; change those service names if the images use different software.
5. Verify the listed hash roots and keep every `allow_pause` value false until
   service recovery is rehearsed.

Global fields:

| Field | Meaning |
| --- | --- |
| `commando_deployment_id` | Stable identifier used for the generated secret and certificate directory. Use a new value for a genuinely new event. |
| `commando_competition_name` | Name displayed in the dashboard. |
| `commando_controller_address` | Fixed OpenStack IP or DNS name of the Red Team Windows VM. |
| `commando_controller_ansible_user` | Administrator used to install the server on the Red-owned Windows VM. |
| `commando_windows_ansible_user` | Packet administrator username shared by the Windows hosts. |
| `commando_linux_ansible_user` | Packet SSH username shared by the Ubuntu hosts. |
| `commando_event_starts_utc` | Exact UTC start in `YYYY-MM-DDTHH:MM:SSZ` form. |
| `commando_event_expires_utc` | Exact UTC end in the same form. It must be later than the start. |
| `commando_require_source_ip` | Keep `true` when the server sees each agent's real address. |
| `commando_server_port` | HTTPS dashboard and agent API port. The packet inventory uses `443`. |

Server host fields:

| Field | Meaning |
| --- | --- |
| Inventory host name | Ansible alias, such as `commando-controller`. |
| `ansible_host` | Address used by Ansible to reach the Windows server. |
| `ansible_user` | Windows administrator account used for installation. |
| `ansible_connection` | Must be `winrm` for the supplied server role. |
| `commando_public_host` | Optional DNS name or address agents use when it differs from `ansible_host`. It is included in the generated TLS certificate. |

Agent host fields:

| Field | Meaning |
| --- | --- |
| `ansible_host` | Address used by Ansible and, by default, the exact authorized policy address. |
| `commando_host_address` | Optional exact policy/source address when it differs from the Ansible connection address. |
| `commando_agent_name` | Unique display and policy name. |
| `commando_agent_hostname` | Optional actual machine hostname. It defaults to `commando_agent_name`; set it when they differ. |
| `commando_allowed_hash_roots` | Local directory roots within which the dashboard may request SHA-256 file hashes. |
| `commando_services` | Locally approved Windows services or Linux systemd units. |
| `commando_allow_unrestricted_powershell` | Windows only. Leave `false` unless arbitrary PowerShell is explicitly authorized. |

Each service entry has these fields:

```yaml
commando_services:
  - id: web
    service_name: W3SVC       # or apache2 on Ubuntu
    tcp_port: 80              # 0 disables the local TCP check
    allow_pause: false
```

`id` is the dashboard label. `service_name` must be the exact Windows service
name or systemd unit name. Keep `allow_pause: false` until pause and automatic
recovery have been tested on a clone of that host.

When a platform group has no hosts, keep the group and use an empty mapping:

```yaml
linux_agents:
  hosts: {}
```

### 4. Handle connection credentials

SSH keys or Kerberos are preferred because the inventory can remain free of
connection passwords. The Bravo Linux controller already uses `bravo-key` for
its own access. To test packet credentials interactively, run:

```bash
bash ansible/deploy.sh --preflight --ask-pass
```

The preflight prompts separately while testing the Linux hosts, Red Windows
controller, and Windows packet hosts. It does not install Commando.

For the full playbook, different per-host passwords should be stored as encrypted
Ansible Vault values in the same `inventory.yml`, then run:

```bash
bash ansible/deploy.sh --ask-vault-pass --ask-become-pass
```

Do not commit unencrypted `ansible_password` or `ansible_become_password` values.
One playbook-level `--ask-pass` value cannot represent different passwords on
every host.

### 5. Deploy

From the repository root on `linux-server-1`, run:

```bash
bash ansible/deploy.sh --ask-vault-pass --ask-become-pass
```

The launcher automatically uses `ansible/inventory.yml`. Do not use a play limit
for the first deployment because local certificate preparation, the server, and
every agent play must run.

The deployment then:

1. Validates the inventory, event dates, addresses, and host count.
2. Generates an operator key, enrollment master key, private CA, server
   certificate, and PFX under `.generated/<commando_deployment_id>/`.
3. Builds the server policy and per-host enrollment keys from the inventory.
4. Installs and starts the Windows server as a restartable SYSTEM boot task.
5. Installs the generated CA and agents on each Windows and Ubuntu host.
6. Starts the agents and waits for their enrollment state.
7. Checks the server dashboard and reports its URL and operator-key path.

Successful completion ends with output similar to:

```text
Dashboard: https://<openstack-controller>:443
Operator key: .../ansible/.generated/competition-one-2026/operator-key
```

### 6. Trust the dashboard certificate

Agents trust the generated private CA automatically. The operator's browser
workstation does not. Securely copy this file to the operator workstation and
install it through the organization's trusted-root process:

```text
ansible/.generated/<commando_deployment_id>/ca.crt
```

Verify that the dashboard host name or address matches the inventory before
trusting the certificate. Do not distribute `ca.key`, `enrollment-key`,
`pfx-password`, or `server.pfx`.

### 7. Open and authenticate to the dashboard

1. Open the reported HTTPS dashboard URL.
2. Read the key on `linux-server-1`:

   ```bash
   cat ansible/.generated/competition-one-2026/operator-key
   ```

3. Paste it into **Operator key** and select **Connect**.
4. Confirm every expected agent appears under **Agent sessions**.

The operator key is retained only in the current browser tab. Keep it out of
shell history, tickets, chat messages, and source control.

### 8. Use an agent session

Select an agent to see its metadata, approved actions, configured services, and
task timeline.

| Dashboard action | Result |
| --- | --- |
| **System info** | Returns operating-system and machine information. |
| **Processes** | Returns a process inventory. |
| **Services** | Returns Windows services or Linux systemd units. |
| **Connections** | Returns active network listeners and connections. |
| **Hash file** | Calculates SHA-256 only for a path inside an approved hash root. |
| **Check status** | Checks an approved service and optional local TCP port. |
| **Pause, then restore** | Stops an explicitly enabled service for 5–60 seconds and attempts recovery. |
| **Restricted PowerShell** | Runs one compiled, read-only command on Windows with validated arguments. |
| **Unrestricted PowerShell** | Appears only when explicitly enabled; runs as SYSTEM and records the script and output. |
| **Stop agent** | Stops that agent's polling; it does not uninstall files or its startup entry. |

Task results appear in the selected agent's timeline. PowerShell transcripts
are also written to the protected transcript directory on the Windows agent.

`Test-Connection` accepts only an exact in-scope target derived from agent
addresses. File hashing rejects paths outside `commando_allowed_hash_roots` and
rejects traversal through symbolic links or Windows reparse points.

### 9. Understand event controls

- Before the configured start, the dashboard reports `PRE COMPETITION` and
  ordinary tasks are unavailable.
- During the event it reports `LIVE`.
- **Pause all** persists a pause and cancels queued work. An active task stops at
  its next control check and attempts any required service restoration.
- **Resume** permits new work; cancelled tasks are not restarted.
- **Emergency stop all** requires typing `STOP COMMANDO`. It cancels queued
  work, queues a stop for every active agent, blocks enrollment, and is
  intentionally one-way for that server event state.
- At the configured expiration time, ordinary work ends automatically.

Use **Pause all** for a temporary halt. Use **Emergency stop all** only when the
event must be irreversibly stopped.

### 10. Rerun or change the deployment

The playbook is designed to be rerun with the same inventory:

```bash
bash ansible/deploy.sh --ask-vault-pass --ask-become-pass
```

It reuses the secrets and CA associated with `commando_deployment_id`, updates
changed configuration or executables, restarts affected components, and repeats
health checks.

Changing `commando_deployment_id` creates a separate identity and new keys. An
agent with an existing state file will continue trying its old token, so a new
event identity requires deliberately archiving the old server data and agent
state before redeployment. Do not delete those records during an active event.

Back up `.generated/<commando_deployment_id>/` securely. Losing it prevents the
playbook from reproducing the same keys and certificate identity.

### 11. Installed locations

| Component | Location |
| --- | --- |
| Windows server executable | `C:\Program Files\Commando\Server` |
| Windows server configuration | `C:\ProgramData\Commando\Server\config` |
| Windows server data | `C:\ProgramData\Commando\Server\data` |
| Windows server logs | `C:\ProgramData\Commando\Server\logs` |
| Windows agent executable | `C:\Program Files\Commando\Agent` |
| Windows agent configuration | `C:\ProgramData\Commando\Agent\config` |
| Windows agent state | `C:\ProgramData\Commando\Agent\state` |
| Windows agent logs/transcripts | `C:\ProgramData\Commando\Agent\logs` |
| Ubuntu agent executable | `/opt/commando` |
| Ubuntu agent configuration | `/etc/commando` |
| Ubuntu agent state | `/var/lib/commando` |
| Ubuntu agent logs/transcripts | `/var/log/commando` |

Windows components appear in Task Scheduler as **Commando Server** and
**Commando Agent**. The Ubuntu systemd unit is `commando-agent`.

### 12. Troubleshooting

**Ansible cannot reach Windows**

- Confirm `ansible_host`, `ansible_user`, WinRM transport, firewall rules, and
  routing from `linux-server-1`.
- Confirm the account has administrator rights.
- Supply the connection password interactively or as an encrypted Vault value.

**Ansible cannot use sudo on Ubuntu**

- Confirm the inventory user can use `sudo`.
- Use `--ask-become-pass` when sudo requires a password.

**The server health check fails**

- Inspect `C:\ProgramData\Commando\Server\logs\server.log`.
- Confirm TCP 443, or the configured replacement, is not already occupied.
- Confirm the event end is later than its start and both use exact UTC format.

**An agent does not appear**

- Confirm its actual hostname matches `commando_agent_hostname`.
- Confirm it reaches the HTTPS server URL and trusts the generated CA.
- With source-IP enforcement enabled, confirm the server sees the exact
  `commando_host_address`.
- Inspect the agent log and its startup task or systemd status.

**A hash request is rejected**

- Use an absolute path under one of that host's
  `commando_allowed_hash_roots` values.
- Confirm the agent account can read the file.

**A service pause is rejected**

- Confirm the service ID is listed for that exact host.
- Confirm `allow_pause: true` was set only after recovery testing.
- Keep the agent task timeout at least 20 seconds longer than the requested
  pause.

**The browser warns about TLS**

- Install the generated `ca.crt` on the operator workstation.
- Open the exact DNS name or IP represented by the inventory-derived
  certificate. Do not replace the URL with an unrelated alias.

### 13. Apply the Bravo infrastructure network rule

The current Bravo Terraform security group permits unrestricted ingress only
from `10.110.30.0/24`. Agents on the scored subnets also need to initiate HTTPS
connections to `win-server-1`. Add these rules to the Bravo infrastructure
Terraform and apply it before deploying Commando:

```hcl
resource "openstack_networking_secgroup_rule_v2" "commando_linux_agents_https" {
  direction         = "ingress"
  ethertype         = "IPv4"
  protocol          = "tcp"
  port_range_min    = 443
  port_range_max    = 443
  remote_ip_prefix  = "10.110.10.0/24"
  security_group_id = openstack_networking_secgroup_v2.bravo_sg.id
}

resource "openstack_networking_secgroup_rule_v2" "commando_windows_agents_https" {
  direction         = "ingress"
  ethertype         = "IPv4"
  protocol          = "tcp"
  port_range_min    = 443
  port_range_max    = 443
  remote_ip_prefix  = "10.110.20.0/24"
  security_group_id = openstack_networking_secgroup_v2.bravo_sg.id
}
```

The public infrastructure repository does not define the direct competition
bridge or routes to `10.110.10.0/24` and `10.110.20.0/24`. Confirm those routes
exist before deployment. The playbook also requires an approved WinRM listener
on `win-server-1` and each Windows packet host. From an administrator PowerShell
session reached through RustDesk, inspect the controller with:

```powershell
winrm enumerate winrm/config/listener
```

Use the environment's approved WinRM configuration. If its port, transport, or
certificate policy differs from `inventory.yml`, update the inventory rather
than weakening the listener globally.

### 14. Test the deployment safely

Run tests in this order. Keep unrestricted PowerShell and every service pause
disabled during the initial validation.

#### A. Test the bundled release locally

On a development machine with PowerShell and .NET installed:

```powershell
pwsh -NoProfile -File scripts/SmokeTest.ps1 -UseRelease
```

This exercises enrollment scope, signed tasks, read-only actions, pause/resume,
service checks, command validation, and emergency-stop behavior using local test
processes. It does not contact competition hosts.

#### B. Test management connectivity without installing anything

On `linux-server-1`:

```bash
sudo apt-get update
sudo apt-get install -y python3 python3-venv
bash ansible/deploy.sh --preflight --ask-pass
```

The preflight installs only the controller-side Ansible dependencies, prints the
inventory graph, then runs Ansible ping tests against the Linux packet hosts,
`win-server-1`, and the Windows packet hosts. It does not execute the deployment
roles. All ten packet hosts plus the controller must respond before continuing.

#### C. Deploy with non-destructive settings

Confirm these remain false for every Windows host and service:

```yaml
commando_allow_unrestricted_powershell: false
allow_pause: false
```

Store differing host credentials as encrypted Vault values, then deploy:

```bash
bash ansible/deploy.sh --ask-vault-pass --ask-become-pass
```

The playbook must finish with no failed hosts and report the dashboard as
`https://10.110.30.21:443`.

#### D. Verify the server and callback path

From `linux-server-1`, verify the generated certificate and public status API:

```bash
curl --fail --cacert ansible/.generated/competition-one-2026/ca.crt \
  https://10.110.30.21/api/status
```

Through RustDesk on `win-server-1`, use administrator PowerShell:

```powershell
Get-ScheduledTask -TaskName 'Commando Server'
Test-NetConnection 127.0.0.1 -Port 443
Get-Content 'C:\ProgramData\Commando\Server\logs\server.log' -Tail 50
```

The scheduled task should be running, TCP 443 should succeed, and the log should
not contain startup or policy errors.

#### E. Verify all agents in the dashboard

Open `https://10.110.30.21` in the browser on `win-server-1`. The playbook trusts
the generated CA on that machine. Enter the operator key stored at:

```text
ansible/.generated/competition-one-2026/operator-key
```

Confirm all ten expected sessions appear: Coruscant, Naboo, Kamino, Felucia,
Alderaan, Geonosis, Mandalore, Kashyyyk, Utapau, and Mustafar.

For each agent, run only these initial checks:

1. **System info**
2. **Processes**
3. **Services**
4. **Connections**
5. **Check status** for its packet service

Every task should progress from queued to completed and show output in the task
timeline.

#### F. Verify controls reject out-of-scope work

- Request a hash inside an approved root and confirm it succeeds.
- Request a hash outside every approved root and confirm it is rejected.
- Confirm **Pause, then restore** is rejected while `allow_pause` is false.
- Confirm the unrestricted PowerShell editor is hidden while its inventory flag
  is false.
- Select **Pause all**, verify queued work is cancelled, then select **Resume**
  and confirm new read-only work completes.

#### G. Test restart recovery

During a rehearsal, restart one agent host at a time. Confirm its Scheduled Task
or systemd unit starts automatically and the same session returns to the
dashboard. Restart `win-server-1` last and confirm the server task, dashboard,
data, and audit timeline return.

#### H. Test a service pause only on a disposable clone

After Grey Team confirms the exact service name and recovery check, enable
`allow_pause: true` for one cloned host, redeploy, and test the minimum five
seconds. Verify both the local service and its application check recover before
enabling any scored host.

Use **Emergency stop all** only as the final rehearsal because it is one-way for
that event state. A subsequent test requires a fresh deployment identity and
cleanly archived server/agent state.

