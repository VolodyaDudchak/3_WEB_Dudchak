let token = localStorage.getItem('token');
let role = localStorage.getItem('role');
let maxN = Number(localStorage.getItem('maxN') || 2500);
let timer = null;

async function api(url, opts = {}) {
  opts.headers = { 'Content-Type': 'application/json', ...(opts.headers || {}) };
  if (token) opts.headers['Authorization'] = 'Bearer ' + token;
  return fetch(url, opts);
}

async function register() {
  const res = await api('/api/auth/register', {
    method: 'POST',
    body: JSON.stringify({ username: user.value, password: pass.value })
  });
  authMsg.textContent = res.ok ? 'Registered. Now login.' : 'Error registering.';
}

async function login() {
  const res = await api('/api/auth/login', {
    method: 'POST',
    body: JSON.stringify({ username: user.value, password: pass.value })
  });
  if (!res.ok) { authMsg.textContent = 'Invalid credentials'; return; }
  const data = await res.json();
  token = data.token; role = data.role; maxN = data.maxN;
  localStorage.setItem('token', token);
  localStorage.setItem('role', role);
  localStorage.setItem('maxN', maxN);
  initApp();
}

function logout() {
  localStorage.clear();
  location.reload();
}

async function startTask() {
  const n = Number(inpN.value);
  const seed = Number(inpSeed.value);
  if (n < 2 || n > maxN) {
    taskMsg.textContent = `Limit exceeded: N must be 2..${maxN}`;
    return;
  }
  const res = await api('/api/tasks', { method: 'POST', body: JSON.stringify({ n, seed }) });
  taskMsg.textContent = res.status === 429 ? 'Max active tasks limit reached' : (res.ok ? 'Queued' : 'Error');
  refresh();
}

async function cancelTask(id) {
  await api(`/api/tasks/${id}/cancel`, { method: 'POST' });
  refresh();
}

async function refresh() {
  const res = await api('/api/tasks');
  if (res.status === 401) return logout();
  const tasks = await res.json();
  tasksBody.innerHTML = tasks.map(t => `
    <tr>
      <td>${t.id}</td>
      <td>${t.n}</td>
      <td>${t.status}</td>
      <td>${t.status === 'Queued' ? `Pos #${t.queuePos}, ~${t.etaSec}s` : t.progress + '%'}</td>
      <td>${t.worker || '-'}</td>
      <td>${t.result || t.error || '-'}</td>
      <td>${['Queued','Running'].includes(t.status) ? `<button onclick="cancelTask(${t.id})">Cancel</button>` : ''}</td>
    </tr>
  `).join('');

  if (role === 'Admin') {
    const ares = await api('/api/admin/stats');
    if (ares.ok) {
      const stats = await ares.json();
      workersBody.innerHTML = stats.workers.map(w =>
        `<tr><td>${w.id}</td><td>${w.node}</td><td>${w.cpu}</td><td>${w.ram}</td><td>${w.active}</td></tr>`
      ).join('');
      allTasksBody.innerHTML = stats.activeTasks.map(t =>
        `<tr><td>${t.id}</td><td>${t.user}</td><td>${t.n}</td><td>${t.status}</td><td>${t.progress}%</td><td>${t.worker}</td></tr>`
      ).join('');
    }
  }
}

function initApp() {
  if (!token) return;
  authBox.classList.add('hidden');
  appBox.classList.remove('hidden');
  if (role === 'Admin') adminBox.classList.remove('hidden');
  refresh();
  clearInterval(timer);
  timer = setInterval(refresh, 1500);
}

initApp();