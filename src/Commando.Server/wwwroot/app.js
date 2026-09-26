const state = { key: '', agents: [], selectedAgentId: null, eventState: 'unknown' };
const $ = (selector) => document.querySelector(selector);

async function api(path, options = {}) {
  const headers = { ...(options.headers || {}) };
  if (state.key) headers['X-Operator-Key'] = state.key;
  if (options.body) headers['Content-Type'] = 'application/json';
  const response = await fetch(path, { ...options, headers });
  if (!response.ok) {
    const detail = await response.text();
    throw new Error(`${response.status} ${detail || response.statusText}`);
  }
  return response.status === 204 ? null : response.json();
}

function notify(message, error = false) {
  const toast = document.createElement('div');
  toast.className = `toast${error ? ' error' : ''}`;
  toast.textContent = message;
  document.body.appendChild(toast);
  setTimeout(() => toast.remove(), 3500);
}

async function loadStatus() {
  try {
    const status = await api('/api/status');
    const node = $('#serverStatus');
    state.eventState = status.control.state;
    node.className = state.eventState === 'live' ? 'pill online' : 'pill';
    node.textContent = `${status.name} · ${state.eventState.replaceAll('_', ' ').toUpperCase()} · ends ${new Date(status.control.expiresUtc).toLocaleString()}`;
    $('#emergencyStopButton').disabled = state.eventState === 'emergency_stopped' || state.eventState === 'ended';
    $('#pauseButton').hidden = state.eventState !== 'live';
    $('#resumeButton').hidden = state.eventState !== 'paused';
  } catch {
    $('#serverStatus').textContent = 'Server unavailable';
  }
}

async function loadAgents() {
  if (!state.key) return;
  try {
    state.agents = await api('/api/operator/agents');
    renderAgents();
    if (state.selectedAgentId) await loadTasks();
  } catch (error) {
    notify(`Could not load sessions: ${error.message}`, true);
  }
}

function renderAgents() {
  const container = $('#agents');
  container.innerHTML = '';
  if (!state.agents.length) {
    container.className = 'agent-list empty';
    container.textContent = 'No enrolled agents.';
    return;
  }
  container.className = 'agent-list';
  for (const agent of state.agents) {
    const fragment = $('#agentTemplate').content.cloneNode(true);
    const button = fragment.querySelector('button');
    button.classList.toggle('selected', agent.id === state.selectedAgentId);
    fragment.querySelector('.agent-name').textContent = agent.name;
    fragment.querySelector('.agent-host').textContent = `${agent.hostname} · ${agent.userName}`;
    fragment.querySelector('.agent-seen').textContent = `Seen ${new Date(agent.lastSeenUtc).toLocaleString()}`;
    button.addEventListener('click', () => selectAgent(agent.id));
    container.appendChild(fragment);
  }
}

async function selectAgent(id) {
  state.selectedAgentId = id;
  const agent = state.agents.find((item) => item.id === id);
  $('#emptyState').hidden = true;
  $('#agentWorkspace').hidden = false;
  $('#selectedAgentName').textContent = agent.name;
  $('#selectedAgentMeta').textContent = `${agent.hostname} · ${agent.osDescription} · ${agent.architecture} · ${agent.remoteAddress}`;
  $('#powerShellSection').hidden = agent.osFamily !== 'Windows';
  const serviceSelect = $('#serviceId');
  serviceSelect.replaceChildren();
  for (const id of agent.allowedServiceIds) {
    const option = document.createElement('option');
    option.value = id;
    option.textContent = id;
    serviceSelect.appendChild(option);
  }
  $('#serviceStatusButton').disabled = !agent.allowedServiceIds.length;
  $('#servicePauseButton').disabled = !agent.allowedServiceIds.length;
  renderAgents();
  await loadTasks();
}

async function queueTask(kind, parameters = {}) {
  if (!state.selectedAgentId) return;
  if (state.eventState !== 'live' && kind !== 'stop_agent') return notify(`Event is ${state.eventState}; tasks are paused.`, true);
  try {
    await api(`/api/operator/agents/${state.selectedAgentId}/tasks`, {
      method: 'POST', body: JSON.stringify({ kind, parameters })
    });
    notify(`${kind} queued`);
    await loadTasks();
  } catch (error) {
    notify(`Task rejected: ${error.message}`, true);
  }
}

async function loadTasks() {
  if (!state.selectedAgentId) return;
  try {
    const tasks = await api(`/api/operator/tasks?agentId=${state.selectedAgentId}`);
    const container = $('#tasks');
    container.innerHTML = '';
    if (!tasks.length) {
      container.innerHTML = '<div class="empty">No tasks have been queued.</div>';
      return;
    }
    for (const task of tasks) {
      const details = document.createElement('details');
      details.className = 'task';
      const output = task.outputJson || task.error || task.parametersJson || '{}';
      details.innerHTML = `<summary><span class="task-title"></span><span class="task-status"></span></summary><pre></pre>`;
      details.querySelector('.task-title').textContent = task.kind;
      const status = details.querySelector('.task-status');
      status.textContent = task.status;
      status.classList.add(task.status);
      try { details.querySelector('pre').textContent = JSON.stringify(JSON.parse(output), null, 2); }
      catch { details.querySelector('pre').textContent = output; }
      container.appendChild(details);
    }
  } catch (error) {
    notify(`Could not load tasks: ${error.message}`, true);
  }
}

$('#connectButton').addEventListener('click', async () => {
  state.key = $('#operatorKey').value;
  await loadAgents();
});
$('#refreshButton').addEventListener('click', loadAgents);
document.querySelectorAll('[data-task]').forEach((button) =>
  button.addEventListener('click', () => queueTask(button.dataset.task)));
$('#hashButton').addEventListener('click', () => {
  const path = $('#hashPath').value.trim();
  if (!path) return notify('Enter an approved local path.', true);
  queueTask('hash_file', { path });
});
$('#stopButton').addEventListener('click', () => {
  if (confirm('Stop this agent after its next check-in?')) queueTask('stop_agent');
});
$('#serviceStatusButton').addEventListener('click', () => queueTask('service_status', { serviceId: $('#serviceId').value }));
$('#servicePauseButton').addEventListener('click', () => {
  const serviceId = $('#serviceId').value;
  const durationSeconds = Number($('#serviceDuration').value);
  if (!serviceId || !Number.isInteger(durationSeconds) || durationSeconds < 5 || durationSeconds > 60)
    return notify('Choose a service and duration from 5 to 60 seconds.', true);
  if (confirm(`Pause ${serviceId} for ${durationSeconds} seconds, then restore it?`))
    queueTask('service_pause', { serviceId, durationSeconds });
});

const powerShellArgumentConfig = {
  'Get-ComputerInfo': { disabled: true, placeholder: 'No arguments required' },
  'Get-Process': { key: 'name', placeholder: 'Optional process name' },
  'Get-Service': { key: 'name', placeholder: 'Optional service name' },
  'Get-HotFix': { key: 'id', placeholder: 'Optional hotfix ID' },
  'Get-NetTCPConnection': { key: 'state', placeholder: 'Optional state, e.g. Listen' },
  'Test-Connection': { key: 'target', placeholder: 'Required in-scope target', count: true }
};

function updatePowerShellForm() {
  const selected = powerShellArgumentConfig[$('#powerShellCommand').value];
  const input = $('#powerShellArgument');
  input.value = '';
  input.disabled = Boolean(selected.disabled);
  input.placeholder = selected.placeholder;
  $('#powerShellCount').hidden = !selected.count;
}

$('#powerShellCommand').addEventListener('change', updatePowerShellForm);
$('#powerShellButton').addEventListener('click', () => {
  const command = $('#powerShellCommand').value;
  const definition = powerShellArgumentConfig[command];
  const value = $('#powerShellArgument').value.trim();
  if (command === 'Test-Connection' && !value) return notify('Test-Connection requires an in-scope target.', true);
  const arguments = {};
  if (definition.key && value) arguments[definition.key] = value;
  if (definition.count) arguments.count = Number($('#powerShellCount').value || 1);
  queueTask('powershell_readonly', { command, arguments });
});

$('#emergencyStopButton').addEventListener('click', async () => {
  const confirmation = prompt('Type STOP COMMANDO to cancel pending work and stop every active agent.');
  if (confirmation !== 'STOP COMMANDO') return;
  try {
    const result = await api('/api/operator/emergency-stop', {
      method: 'POST', body: JSON.stringify({ confirmation })
    });
    state.eventState = 'emergency_stopped';
    notify(`Emergency stop queued for ${result.agentsTargeted} agent(s).`);
    await loadStatus();
    await loadAgents();
  } catch (error) {
    notify(`Emergency stop failed: ${error.message}`, true);
  }
});

$('#pauseButton').addEventListener('click', async () => {
  try {
    await api('/api/operator/pause', { method: 'POST' });
    notify('Event paused; queued tasks cancelled.');
    await loadStatus();
    await loadTasks();
  } catch (error) { notify(`Pause failed: ${error.message}`, true); }
});

$('#resumeButton').addEventListener('click', async () => {
  try {
    await api('/api/operator/resume', { method: 'POST' });
    notify('Event resumed.');
    await loadStatus();
  } catch (error) { notify(`Resume failed: ${error.message}`, true); }
});

loadStatus();
updatePowerShellForm();
setInterval(() => { loadStatus(); if (state.key) loadAgents(); }, 5000);
