param([int]$Port = 5097, [switch]$UseRelease, [switch]$UseNatPolicy)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$work = Join-Path $root ("tmp\smoke-$([Guid]::NewGuid().ToString('N'))")
New-Item -ItemType Directory -Force -Path $work | Out-Null
$serverProcess = $null

function Invoke-AgentOnce {
    if ($UseRelease) {
        & $agentExecutable --config $agentPath --once | Out-Null
    } else {
        & dotnet $agentExecutable --config $agentPath --once | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw "Agent run failed: $LASTEXITCODE" }
}

try {
    $operatorKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $enrollmentKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $hostEnrollmentKey = [Convert]::ToBase64String([Security.Cryptography.HMACSHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($enrollmentKey), [Text.Encoding]::UTF8.GetBytes('Commando:host:localtest')))
    $now = [DateTimeOffset]::UtcNow
    $network = if ($UseNatPolicy) { '10.110.20.0/24' } else { '127.0.0.0/8' }
    $policy = @{
        startsUtc = $now.AddMinutes(-1).ToString('O')
        expiresUtc = $now.AddMinutes(10).ToString('O')
        requireSourceIp = (-not $UseNatPolicy)
        allowedNetworks = @($network)
        hosts = @(@{
            name = 'LocalTest'
            hostname = [Environment]::MachineName
            address = if ($UseNatPolicy) { '10.110.20.12' } else { '127.0.0.1' }
            osFamily = 'Windows'
            allowedServiceIds = @('eventlog')
        })
    }
    $policyPath = Join-Path $work 'policy.json'
    $policy | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $policyPath -Encoding utf8
    $agentConfig = @{
        serverUrl = "http://127.0.0.1:$Port"
        enrollmentKey = $hostEnrollmentKey
        agentName = 'LocalTest'
        pollSeconds = 2
        taskTimeoutSeconds = 30
        allowInsecureLoopback = $true
        statePath = (Join-Path $work 'agent-state.json')
        recoveryPath = (Join-Path $work 'agent-recovery.json')
        allowedHashRoots = @($work)
        transcriptDirectory = (Join-Path $work 'transcripts')
        powerShellExecutable = 'powershell.exe'
        enabledPowerShellCommands = @()
        allowedTargets = @('127.0.0.1')
        allowedCidrs = @()
        services = @(@{ id = 'eventlog'; serviceName = 'EventLog'; tcpPort = 0; allowPause = $false })
    }
    $agentPath = Join-Path $work 'agent.json'
    $agentConfig | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $agentPath -Encoding utf8

    $env:COMMANDO_POLICY_FILE = $policyPath
    $env:COMMANDO_OPERATOR_KEY = $operatorKey
    $env:COMMANDO_ENROLLMENT_KEY = $enrollmentKey
    $env:Commando__DataDirectory = (Join-Path $work 'server-data')
    $serverExecutable = if ($UseRelease) { Join-Path $root 'artifacts\release\server-win-x64\Commando.Server.exe' } else { Join-Path $root 'src\Commando.Server\bin\Debug\net9.0\Commando.Server.dll' }
    $agentExecutable = if ($UseRelease) { Join-Path $root 'artifacts\release\agent-win-x64\Commando.Agent.exe' } else { Join-Path $root 'src\Commando.Agent\bin\Debug\net9.0\Commando.Agent.dll' }
    $serverWorkingDirectory = if ($UseRelease) { Join-Path $root 'artifacts\release\server-win-x64' } else { Join-Path $root 'src\Commando.Server' }
    $serverArguments = if ($UseRelease) { @('--urls', "http://127.0.0.1:$Port") } else { @($serverExecutable, '--urls', "http://127.0.0.1:$Port") }
    $serverProgram = if ($UseRelease) { $serverExecutable } else { 'dotnet' }
    $serverProcess = Start-Process -FilePath $serverProgram -ArgumentList $serverArguments `
        -WorkingDirectory $serverWorkingDirectory -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $work 'server-out.log') -RedirectStandardError (Join-Path $work 'server-err.log')

    $baseUrl = "http://127.0.0.1:$Port"
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($serverProcess.HasExited) { break }
        try {
            $status = Invoke-RestMethod "$baseUrl/api/status" -TimeoutSec 1
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $ready) { throw 'Server did not start.' }
    if ($status.control.state -ne 'live') { throw "Expected live state, got $($status.control.state)." }

    $badRequest = @{ name = 'bad'; hostname = 'outside-scope'; userName = 'test'; osDescription = 'Windows'; architecture = 'X64'; agentVersion = '1'; osFamily = 'Windows' } | ConvertTo-Json
    try {
        Invoke-RestMethod "$baseUrl/api/agent/register" -Method Post -Headers @{ 'X-Enrollment-Key' = $hostEnrollmentKey } -ContentType 'application/json' -Body $badRequest | Out-Null
        throw 'Out-of-scope registration was accepted.'
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 400) { throw }
    }
    $wrongKeyRequest = @{ name = 'LocalTest'; hostname = [Environment]::MachineName; userName = 'test'; osDescription = 'Windows'; architecture = 'X64'; agentVersion = '1'; osFamily = 'Windows' } | ConvertTo-Json
    try {
        Invoke-RestMethod "$baseUrl/api/agent/register" -Method Post -Headers @{ 'X-Enrollment-Key' = $enrollmentKey } -ContentType 'application/json' -Body $wrongKeyRequest | Out-Null
        throw 'Master enrollment key was accepted as a host key.'
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 401) { throw }
    }

    Invoke-AgentOnce
    $headers = @{ 'X-Operator-Key' = $operatorKey }
    $agents = Invoke-RestMethod "$baseUrl/api/operator/agents" -Headers $headers
    if ($agents.Count -ne 1) { throw "Expected one agent, got $($agents.Count)." }
    $agentId = $agents[0].id

    Invoke-RestMethod "$baseUrl/api/operator/pause" -Method Post -Headers $headers | Out-Null
    try {
        Invoke-RestMethod "$baseUrl/api/operator/agents/$agentId/tasks" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"kind":"system_info","parameters":{}}' | Out-Null
        throw 'Task was accepted while paused.'
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 423) { throw }
    }
    Invoke-RestMethod "$baseUrl/api/operator/resume" -Method Post -Headers $headers | Out-Null
    Invoke-RestMethod "$baseUrl/api/operator/agents/$agentId/tasks" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"kind":"system_info","parameters":{}}' | Out-Null
    Invoke-AgentOnce
    $tasks = Invoke-RestMethod "$baseUrl/api/operator/tasks?agentId=$agentId" -Headers $headers
    if ($tasks.Count -ne 1 -or $tasks[0].status -ne 'completed' -or -not $tasks[0].success) {
        throw 'Expected a completed system_info task.'
    }
    Invoke-RestMethod "$baseUrl/api/operator/agents/$agentId/tasks" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"kind":"service_status","parameters":{"serviceId":"eventlog"}}' | Out-Null
    Invoke-AgentOnce
    $tasks = Invoke-RestMethod "$baseUrl/api/operator/tasks?agentId=$agentId" -Headers $headers
    if ($tasks[0].kind -ne 'service_status' -or $tasks[0].status -ne 'completed' -or -not $tasks[0].success) {
        throw 'Expected a completed service_status task.'
    }
    Invoke-RestMethod "$baseUrl/api/operator/agents/$agentId/tasks" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"kind":"service_pause","parameters":{"serviceId":"eventlog","durationSeconds":5}}' | Out-Null
    Invoke-AgentOnce
    $tasks = Invoke-RestMethod "$baseUrl/api/operator/tasks?agentId=$agentId" -Headers $headers
    if ($tasks[0].kind -ne 'service_pause' -or $tasks[0].success) {
        throw 'Expected the locally disabled service pause to be rejected.'
    }
    try {
        Invoke-RestMethod "$baseUrl/api/operator/agents/$agentId/tasks" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"kind":"service_pause","parameters":{"serviceId":"eventlog","durationSeconds":"5"}}' | Out-Null
        throw 'Malformed duration was accepted.'
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 400) { throw }
    }
    try {
        Invoke-RestMethod "$baseUrl/api/operator/agents/$agentId/tasks" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"kind":"powershell_readonly","parameters":{"command":"Test-Connection","arguments":{"target":"10.110.30.11"}}}' | Out-Null
        throw 'Out-of-scope Test-Connection was accepted.'
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 400) { throw }
    }
    Invoke-RestMethod "$baseUrl/api/operator/emergency-stop" -Method Post -Headers $headers -ContentType 'application/json' -Body '{"confirmation":"STOP COMMANDO"}' | Out-Null
    Invoke-AgentOnce
    $status = Invoke-RestMethod "$baseUrl/api/status"
    if ($status.control.state -ne 'emergency_stopped') { throw 'Emergency stop state was not persisted.' }
    Write-Output 'Smoke test passed: scoped enrollment, pause/resume, signed tasks, service checks, local action denial, invalid-input rejection, and emergency stop.'
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force
    }
    Remove-Item Env:COMMANDO_POLICY_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:COMMANDO_OPERATOR_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:COMMANDO_ENROLLMENT_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:Commando__DataDirectory -ErrorAction SilentlyContinue
}
