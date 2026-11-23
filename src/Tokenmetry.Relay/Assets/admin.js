// 运营者管理页（/admin）的页面脚本：登录、成员列表、签发与吊销。
// 页面本体只是匿名空壳，数据全部经 Authorization 头调用 /api/admin/members；
// token 只进当前标签页的 sessionStorage（无 Cookie）。公共工具见 relay-common.js。
(() => {
  'use strict';

  const { byId, resolve, isZh: zh, locale } = window.TokenmetryRelay;

  const text = zh
    ? {
        title: '中继管理',
        logout: '退出',
        loginTitle: '管理登录',
        loginHint: '输入运营者的 TOKENMETRY_RELAY_TOKEN。成员 token 会被拒绝。',
        tokenLabel: '运营者 token',
        enter: '进入',
        badToken: 'token 不正确，或该中继没有设置 TOKENMETRY_RELAY_TOKEN。',
        memberTokenRejected: '成员 token 不能进入管理页；请使用运营者 token。',
        sessionExpired: '登录已失效，请重新输入运营者 token。',
        loading: '加载中…',
        error: '操作失败：',
        members: '成员',
        devices: '设备',
        addTitle: '添加成员',
        addHint: '创建后 token 只显示一次；把中继地址和 token 一起发给对方。',
        memberName: '展示名',
        memberNamePlaceholder: '例如 Alice',
        add: '添加',
        creating: '创建中…',
        created: '成员已创建',
        createdHint: 'token 只显示这一次，请立即保存。',
        copy: '复制',
        copied: '已复制',
        copyManual: '已选中，请手动复制',
        saved: '我已保存',
        membersTitle: '成员',
        refresh: '刷新',
        colName: '成员',
        colDevices: '设备',
        colLastSeen: '最后活跃',
        colCreated: '创建时间',
        revoke: '吊销',
        confirmRevoke: '确定吊销成员「{name}」？其同步数据、设备与榜单记录会一并删除，无法恢复。',
        empty: '还没有成员。',
        footer: '管理页面只使用运营者 token；成员 token 不能访问。登录 token 只保存在当前标签页。',
        leaderboard: '查看排行榜'
      }
    : {
        title: 'Relay Admin',
        logout: 'Sign out',
        loginTitle: 'Admin sign-in',
        loginHint: 'Enter the operator TOKENMETRY_RELAY_TOKEN. Member tokens are rejected.',
        tokenLabel: 'Operator token',
        enter: 'Sign in',
        badToken: 'The token is wrong, or this relay has no TOKENMETRY_RELAY_TOKEN configured.',
        memberTokenRejected: 'A member token cannot access the admin page. Use the operator token.',
        sessionExpired: 'Session expired. Enter the operator token again.',
        loading: 'Loading…',
        error: 'Request failed:',
        members: 'Members',
        devices: 'Devices',
        addTitle: 'Add member',
        addHint: 'The token is shown once; send it to your friend together with the relay URL.',
        memberName: 'Display name',
        memberNamePlaceholder: 'e.g. Alice',
        add: 'Add',
        creating: 'Creating…',
        created: 'Member created',
        createdHint: 'The token is shown only once. Save it now.',
        copy: 'Copy',
        copied: 'Copied',
        copyManual: 'Selected — copy manually',
        saved: 'I saved it',
        membersTitle: 'Members',
        refresh: 'Refresh',
        colName: 'Member',
        colDevices: 'Devices',
        colLastSeen: 'Last seen',
        colCreated: 'Created',
        revoke: 'Revoke',
        confirmRevoke: 'Revoke member "{name}"? Their synced rows, devices and leaderboard records will be deleted permanently.',
        empty: 'No members yet.',
        footer: 'The admin page accepts only the operator token; member tokens are rejected. The token is kept in this tab only.',
        leaderboard: 'View leaderboard'
      };

  document.documentElement.lang = zh ? 'zh-CN' : 'en';
  document.title = text.title;

  const tokenKey = 'tokenmetry.admin.token';

  function readSavedToken() {
    try {
      return sessionStorage.getItem(tokenKey) || '';
    } catch {
      return '';
    }
  }

  function saveToken(value) {
    try {
      sessionStorage.setItem(tokenKey, value);
    } catch {
      // 隐私模式/禁用存储：token 只留在当前页面内存里。
    }
  }

  function forgetToken() {
    try {
      sessionStorage.removeItem(tokenKey);
    } catch {
      // 忽略：内存里的 token 已清。
    }
  }

  let token = '';

  class ApiError extends Error {
    constructor(status, message) {
      super(message);
      this.status = status;
    }
  }

  async function api(path, options = {}) {
    const headers = new Headers(options.headers || {});
    headers.set('Authorization', `Bearer ${token}`);
    if (options.body) {
      headers.set('Content-Type', 'application/json');
    }

    const response = await fetch(resolve(path), { ...options, headers });
    if (response.status === 204) {
      return null;
    }

    const body = await response.text();
    let data = null;
    try {
      data = body ? JSON.parse(body) : null;
    } catch {
      // 非 JSON 错误体：保留状态码信息。
    }

    if (!response.ok) {
      throw new ApiError(response.status, (data && data.error) || `HTTP ${response.status}`);
    }

    return data;
  }

  // ---- DOM ----
  const title = byId('title');
  const logoutButton = byId('logout');
  const loginView = byId('login-view');
  const appView = byId('app-view');
  const loginForm = byId('login-form');
  const loginStatus = byId('login-status');
  const tokenInput = byId('token-input');
  const addForm = byId('add-form');
  const memberName = byId('member-name');
  const addButton = byId('add-button');
  const addStatus = byId('add-status');
  const newTokenPanel = byId('new-token');
  const newTokenTitle = byId('new-token-title');
  const newTokenValue = byId('new-token-value');
  const copyButton = byId('copy-token');
  const dismissButton = byId('dismiss-token');
  const membersStatus = byId('members-status');
  const memberRows = byId('member-rows');
  const membersEmpty = byId('members-empty');
  const statMembers = byId('stat-members');
  const statDevices = byId('stat-devices');
  const refreshButton = byId('refresh');
  const leaderboardLink = byId('leaderboard-link');

  const members = [];

  // ---- 文案 ----
  title.textContent = text.title;
  logoutButton.textContent = text.logout;
  byId('login-title').textContent = text.loginTitle;
  byId('login-hint').textContent = text.loginHint;
  byId('token-label').textContent = text.tokenLabel;
  byId('login-button').textContent = text.enter;
  byId('stat-members-label').textContent = text.members;
  byId('stat-devices-label').textContent = text.devices;
  byId('add-title').textContent = text.addTitle;
  byId('add-hint').textContent = text.addHint;
  byId('member-name-label').textContent = text.memberName;
  memberName.placeholder = text.memberNamePlaceholder;
  addButton.textContent = text.add;
  byId('new-token-title').textContent = text.created;
  byId('new-token-hint').textContent = text.createdHint;
  byId('new-token-label').textContent = text.tokenLabel;
  copyButton.textContent = text.copy;
  dismissButton.textContent = text.saved;
  byId('members-title').textContent = text.membersTitle;
  refreshButton.textContent = text.refresh;
  byId('col-name').textContent = text.colName;
  byId('col-devices').textContent = text.colDevices;
  byId('col-last-seen').textContent = text.colLastSeen;
  byId('col-created').textContent = text.colCreated;
  membersEmpty.textContent = text.empty;
  byId('footer-note').textContent = text.footer;
  leaderboardLink.textContent = text.leaderboard;
  leaderboardLink.href = resolve('leaderboard').toString();

  // ---- 视图 ----
  function showLogin(message) {
    loginView.hidden = false;
    appView.hidden = true;
    logoutButton.hidden = true;
    loginStatus.textContent = message || '';
    tokenInput.value = '';
    tokenInput.focus();
  }

  function showApp() {
    loginView.hidden = true;
    appView.hidden = false;
    logoutButton.hidden = false;
    loginStatus.textContent = '';
  }

  function signOut(message) {
    token = '';
    forgetToken();
    members.length = 0;
    renderMembers();
    newTokenPanel.hidden = true;
    newTokenValue.value = '';
    addStatus.textContent = '';
    membersStatus.textContent = '';
    showLogin(message || '');
  }

  function formatDate(value) {
    if (!value) {
      return '—';
    }

    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString(locale);
  }

  function renderMembers() {
    memberRows.replaceChildren();
    let deviceCount = 0;

    for (const member of members) {
      deviceCount += Number(member.deviceCount) || 0;

      const row = document.createElement('tr');

      const nameCell = document.createElement('td');
      const nameWrap = document.createElement('div');
      nameWrap.className = 'member-name';
      const name = document.createElement('strong');
      name.textContent = member.displayName || 'anonymous';
      const accountId = document.createElement('code');
      accountId.textContent = member.accountId;
      nameWrap.append(name, accountId);
      nameCell.append(nameWrap);

      const devicesCell = document.createElement('td');
      devicesCell.className = 'num';
      devicesCell.textContent = String(Number(member.deviceCount) || 0);

      const lastSeenCell = document.createElement('td');
      lastSeenCell.textContent = formatDate(member.lastSeenAt);

      const createdCell = document.createElement('td');
      createdCell.textContent = formatDate(member.createdAt);

      const actionsCell = document.createElement('td');
      actionsCell.className = 'actions';
      const revokeButton = document.createElement('button');
      revokeButton.type = 'button';
      revokeButton.className = 'danger';
      revokeButton.textContent = text.revoke;
      revokeButton.addEventListener('click', () => revokeMember(member, revokeButton));
      actionsCell.append(revokeButton);

      row.append(nameCell, devicesCell, lastSeenCell, createdCell, actionsCell);
      memberRows.append(row);
    }

    statMembers.textContent = String(members.length);
    statDevices.textContent = String(deviceCount);
    membersEmpty.hidden = members.length !== 0;
  }

  function applyMembers(data) {
    members.length = 0;
    if (data && Array.isArray(data.members)) {
      members.push(...data.members);
    }

    renderMembers();
  }

  async function refreshMembers() {
    membersStatus.textContent = text.loading;
    try {
      const data = await api('api/admin/members');
      applyMembers(data);
      membersStatus.textContent = '';
      showApp();
    } catch (error) {
      if (error.status === 401 || error.status === 403) {
        signOut(error.status === 403 ? text.memberTokenRejected : text.sessionExpired);
        return;
      }

      membersStatus.textContent = `${text.error} ${error.message}`;
      showApp();
    }
  }

  async function signIn() {
    const candidate = tokenInput.value.trim();
    if (!candidate) {
      return;
    }

    token = candidate;
    loginStatus.textContent = text.loading;
    try {
      const data = await api('api/admin/members');
      saveToken(token);
      applyMembers(data);
      tokenInput.value = '';
      loginStatus.textContent = '';
      membersStatus.textContent = '';
      showApp();
    } catch (error) {
      token = '';
      loginStatus.textContent = error.status === 403
        ? text.memberTokenRejected
        : error.status === 401
          ? text.badToken
          : `${text.error} ${error.message}`;
    }
  }

  async function addMember() {
    const name = memberName.value.trim();
    if (!name) {
      return;
    }

    addButton.disabled = true;
    addStatus.textContent = text.creating;
    try {
      const added = await api('api/admin/members', {
        method: 'POST',
        body: JSON.stringify({ name })
      });

      newTokenTitle.textContent = `${text.created} · ${added.displayName}`;
      newTokenValue.value = added.token;
      newTokenPanel.hidden = false;
      memberName.value = '';
      addStatus.textContent = '';
      await refreshMembers();
    } catch (error) {
      addStatus.textContent = `${text.error} ${error.message}`;
    } finally {
      addButton.disabled = false;
    }
  }

  async function revokeMember(member, button) {
    const name = member.displayName || member.accountId;
    if (!window.confirm(text.confirmRevoke.replace('{name}', () => name))) {
      return;
    }

    button.disabled = true;
    membersStatus.textContent = text.loading;
    try {
      await api(`api/admin/members/${encodeURIComponent(member.accountId)}`, { method: 'DELETE' });
      await refreshMembers();
    } catch (error) {
      membersStatus.textContent = `${text.error} ${error.message}`;
    } finally {
      button.disabled = false;
    }
  }

  async function copyToken() {
    try {
      await navigator.clipboard.writeText(newTokenValue.value);
      copyButton.textContent = text.copied;
      setTimeout(() => { copyButton.textContent = text.copy; }, 1500);
    } catch {
      newTokenValue.select();
      copyButton.textContent = text.copyManual;
    }
  }

  loginForm.addEventListener('submit', (event) => {
    event.preventDefault();
    signIn();
  });

  addForm.addEventListener('submit', (event) => {
    event.preventDefault();
    addMember();
  });

  logoutButton.addEventListener('click', () => signOut(''));
  refreshButton.addEventListener('click', refreshMembers);
  copyButton.addEventListener('click', copyToken);
  dismissButton.addEventListener('click', () => {
    newTokenPanel.hidden = true;
    newTokenValue.value = '';
  });

  const saved = readSavedToken();
  if (saved) {
    token = saved;
    showApp();
    refreshMembers();
  } else {
    showLogin('');
  }
})();
