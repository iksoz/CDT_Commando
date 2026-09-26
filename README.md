# Commando

Commando is a competition-scoped operations controller. It gives an authorized operator a browser console and lets Windows or Linux agents receive signed, allowlisted tasks. Its event policy names the permitted hosts, service IDs, and start/end times.

It does not provide initial access, arbitrary shell execution, credential collection, autonomous propagation, hidden persistence, or defense disabling. Service pauses are explicit, bounded, and followed by an automatic restoration attempt.

## Current status

The Windows server and Windows/Linux agents have been published as self-contained releases under `artifacts/release/`. The published Windows server and agent passed the local smoke test in both direct-IP and NAT-policy modes. That test covers scoped enrollment, pause/resume, signed tasks, service status, local denial of unapproved service actions, invalid-input rejection, and emergency stop. It does **not** stop a real service.

The Linux agent was cross-published but has not been run on Ubuntu here. Neither the Linux runtime behavior nor a real service stop/restart and recovery cycle has been validated on a competition host. Complete the rehearsal in [Competition deployment](#competition-deployment) before enabling service pauses during the event.

## Included

- ASP.NET Core task server and operator dashboard
- Windows/Linux .NET agent with HTTPS enforcement
- Separate enrollment, operator, and per-agent credentials
- HMAC-signed tasks with short expiry
- Persistent server audit log and task timeline
- Restricted read-only PowerShell execution with structured arguments
- Per-agent command, hostname/IP, and CIDR scope enforcement
- Local JSON PowerShell transcripts with SHA-256 hashes
- Clean per-agent stop and persisted server-wide emergency shutdown
- Persisted pause/resume that cancels queued work and interrupts an active task at its next control check
- Exact host/IP enrollment and task scope from a required event policy file
- Local service status checks and opt-in 5–60 second service pauses with a recovery journal
- Allowlisted actions:
  - System information
  - Process inventory
  - Windows service inventory
  - TCP/UDP connection inventory
  - SHA-256 hashing within organizer-approved roots
  - Restricted PowerShell inventory commands

## Architecture

```text
Operator browser
      |
      | operator-authenticated HTTPS
      v
Commando.Server
      |
      | per-agent HTTPS + signed tasks
      v
Commando.Agent on an authorized Windows or Linux competition host
```

The agent performs only locally configured inventory and service actions. It does not scan for, exploit, or install itself on other machines.

## Requirements

- .NET 9 SDK to build
- Windows Server 2019 or Ubuntu 22.04 for the agent
- An organizer-controlled competition network
- TLS certificate for any non-loopback deployment
- Direct agent-to-server connectivity that preserves source IP by default; a per-host enrollment key supports explicitly configured NAT deployments

## Build

```powershell
dotnet build .\src\Commando.Server\Commando.Server.csproj
dotnet build .\src\Commando.Agent\Commando.Agent.csproj
```

Release builds:

```powershell
dotnet publish .\src\Commando.Server\Commando.Server.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\release\server-win-x64
dotnet publish .\src\Commando.Agent\Commando.Agent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\release\agent-win-x64
dotnet publish .\src\Commando.Agent\Commando.Agent.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o .\artifacts\release\agent-linux-x64
```

The resulting executables are `artifacts/release/server-win-x64/Commando.Server.exe`, `artifacts/release/agent-win-x64/Commando.Agent.exe`, and `artifacts/release/agent-linux-x64/Commando.Agent`. The agent publish directories also contain example configuration files. Publishing is not the same as configuring or installing agents on event hosts.

## Local demonstration

Build both projects, then run `./scripts/SmokeTest.ps1` in PowerShell. Add `-UseRelease` to test the published Windows executables and `-UseNatPolicy` to exercise enrollment when the policy permits NAT. The script creates a temporary policy, launches the server, enrolls an agent, verifies scope and pause behavior, runs signed inventory and service-status tasks, and stops the test server. Test data stays under ignored `tmp/`. It never pauses a real service.

```powershell
.\scripts\SmokeTest.ps1 -UseRelease
.\scripts\SmokeTest.ps1 -UseRelease -UseNatPolicy
```

For an interactive demo, create a policy with `127.0.0.1` and your machine's exact hostname, set `COMMANDO_POLICY_FILE` to its path, set separate `COMMANDO_OPERATOR_KEY` and `COMMANDO_ENROLLMENT_KEY` values of at least 16 characters, and start the server on loopback. Derive a host enrollment key as below, using the policy host name. Set the agent's `serverUrl` to loopback, `allowInsecureLoopback` to `true`, and `enrollmentKey` to that derived key. Then run the agent with `--config <path>` and open the server URL in a browser.

```powershell
$hostName = 'LocalTest'
$message = "Commando:host:$($hostName.ToLowerInvariant())"
$hostKey = [Convert]::ToBase64String([Security.Cryptography.HMACSHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($env:COMMANDO_ENROLLMENT_KEY),
    [Text.Encoding]::UTF8.GetBytes($message)))
```

## Restricted PowerShell

On Windows, Commando does not accept PowerShell script text. The operator selects a compiled read-only command and sends structured arguments. The server validates the request, signs the complete task, and binds it to one agent and expiration time. The agent verifies that signature, repeats the command/argument validation, applies its local target scope, and starts PowerShell without profiles or interactive input. Linux agents reject PowerShell tasks.

The compiled command set is:

```text
Get-ComputerInfo
Get-Process        [-Name]
Get-Service        [-Name]
Get-HotFix         [-Id]
Get-NetTCPConnection [-State]
Test-Connection    -Target [-Count 1..4]
```

`enabledPowerShellCommands` can restrict this set per agent but cannot expand it. `Test-Connection` requires a literal IP in the event host list, and the agent must also allow that IP in `allowedTargets` or `allowedCidrs`. Hostnames are never implicitly resolved to bypass that policy.

Each execution has the configured `taskTimeoutSeconds` limit, capped at 120 seconds. Its command, structured arguments, timing, exit status, standard output, and standard error are written beneath `transcriptDirectory`. The returned result includes the transcript path and SHA-256 hash.

## Service actions

`service_status` reports the state of a locally configured Windows service or Linux systemd unit and optionally tests a local TCP port. `service_pause` stops an active service for 5–60 seconds and then attempts to restart it. Both the server policy and local agent config must list the same `serviceId`; the agent also requires `allowPause: true` for a pause. Tasks accept structured data only:

```json
{ "kind": "service_pause", "parameters": { "serviceId": "iis", "durationSeconds": 15 } }
```

The agent writes a recovery journal before stopping the service. A normal timeout, event pause, or emergency stop triggers immediate restoration. It clears the journal only after the service is active and, when a TCP port is configured, that local port is reachable. If the agent process or host crashes, the agent attempts recovery from the journal on its next launch. Restoration after a host failure therefore depends on the agent being started again; verify service status independently before relying on this action in a live event. `taskTimeoutSeconds` must exceed the requested pause by at least 20 seconds.

Service names and TCP ports in the example configs are **illustrative**. Confirm the actual units, Windows service names, and scored checks with Grey Team before enabling `allowPause` on a host. The status check is a local indicator, not a substitute for the scoring engine.

## Event controls and emergency shutdown

`COMMANDO_POLICY_FILE` is required at server startup. The JSON file must contain explicit `startsUtc`, `expiresUtc`, `allowedNetworks`, and `hosts`. The server rejects an unlisted hostname or OS family and verifies that host's derived enrollment key. With `requireSourceIp: true` (the default), enrollment and polling must also come from the listed IP. Set it to `false` only when NAT or a gateway changes the source address; the host key and issued agent token then authenticate the agent. Before the start time and after the end time, the server does not dispatch ordinary tasks.

**Pause all** persists a pause and cancels queued tasks. An agent executing a task checks server control every two seconds and cancels when it observes a pause or loss of the control connection. **Resume** accepts new tasks; cancelled tasks do not restart automatically. A task already running may take time to finish its restoration step. Network or host failure can delay observation of a pause.

The dashboard's **Emergency stop all** action requires the exact confirmation `STOP COMMANDO`. It then:

1. Persists the emergency-stopped state.
2. Cancels every queued task.
3. Queues a signed `stop_agent` task for every active agent.
4. Rejects new enrollment and operator tasks.
5. Records the action in the audit log.

Emergency stop is intentionally one-way for an event state. To begin a new event, archive the prior data directory for audit purposes and configure a fresh `Commando__DataDirectory`.

## Competition deployment

1. Copy `config/commando-alpha-policy.example.json` to a private event file. Set the exact competition start/end times and verify every host name, address, and service ID. The example dates are illustrative.
2. Set `COMMANDO_POLICY_FILE` to that file's absolute path. Keep `requireSourceIp: true` when agent traffic reaches the server directly. If a gateway changes the source IP, set it to `false` and distribute only the unique derived key for each named host. Never distribute the master `COMMANDO_ENROLLMENT_KEY` to an agent.
3. Place the server behind HTTPS or configure Kestrel with a certificate trusted by the agents.
4. Set `COMMANDO_OPERATOR_KEY` and `COMMANDO_ENROLLMENT_KEY` through the host's secret manager. Restrict `Commando__DataDirectory` to an operator-controlled directory.
5. Create a separate agent config for each host. Derive its enrollment key using the example above and its policy host name. Set the HTTPS `serverUrl`, `allowInsecureLoopback: false`, `agentName`, local service names/ports, and restricted hash roots. Give each agent its own protected state and recovery paths.
6. Keep `allowedTargets` to the exact event host IPs and `allowedCidrs` empty unless a broader range is explicitly authorized.
7. Protect config files, state files, recovery journals, and transcripts with host file permissions. Deploy the agent only through the approved competition access mechanism.
8. On a cloned Windows Server 2019 and Ubuntu 22.04 host, rehearse enrollment, service status, pause/resume, emergency stop, and an approved service pause. Confirm the service and its scored application check recover both normally and after restarting an interrupted agent. Keep `allowPause: false` until this succeeds for each service.

Useful server environment settings:

```text
Commando__CompetitionName
COMMANDO_POLICY_FILE
Commando__DataDirectory
Commando__TaskLifetimeMinutes
Commando__MaxOutputBytes
```

The server refuses to start unless both secrets contain at least 16 characters and a valid policy file with explicit event times is present.

## Operational safety

- Use only on systems explicitly included in written rules of engagement.
- Treat enrollment keys and agent state files as secrets.
- Use a separate operator key and enrollment key.
- Bind the server only to the competition VPN or management network.
- Remove agents and revoke access after the event.
- Keep the server's state file for the event audit record.

## Project layout

```text
src/Commando.Shared   Shared API contracts and task signing
src/Commando.Server   Task broker, state store, and web console
src/Commando.Agent    Windows/Linux agent
config/               Event policy and per-platform agent examples
scripts/              Local smoke test
```
