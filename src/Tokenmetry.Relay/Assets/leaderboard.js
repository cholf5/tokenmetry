// 公开排行榜页（/leaderboard）的页面脚本：读同源匿名 API /api/leaderboard，
// 支持金额榜/Token 榜切换与月份翻页。公共工具见 relay-common.js。
(() => {
  'use strict';

  const { byId, resolve, isZh: zh, locale } = window.TokenmetryRelay;

  const text = zh
    ? {
        title: 'Tokenmetry 排行榜',
        subtitle: '只显示已 opt-in 的账号；数据来自本中继的账号级聚合。',
        cost: '金额榜',
        tokens: 'Token 榜',
        account: '账号',
        tokenColumn: 'Token',
        costColumn: '金额 (USD)',
        prev: '上个月',
        next: '下个月',
        loading: '加载中…',
        error: '加载失败：',
        empty: '这个月还没有人 opt-in 上榜。',
        participant: '人参与',
        participants: '人参与',
        privacy: '只有显式开启排行榜的账号会出现；公开数据只有账号级 token / 成本合计，不含项目或会话明细。',
        source: '数据来自'
      }
    : {
        title: 'Tokenmetry Leaderboard',
        subtitle: 'Only accounts that explicitly opted in; account-level aggregates from this relay.',
        cost: 'Cost',
        tokens: 'Tokens',
        account: 'Account',
        tokenColumn: 'Tokens',
        costColumn: 'Cost (USD)',
        prev: 'Previous month',
        next: 'Next month',
        loading: 'Loading…',
        error: 'Failed to load:',
        empty: 'No one has opted in for this month yet.',
        participant: 'participant',
        participants: 'participants',
        privacy: 'Only accounts that explicitly opted in appear here; public data is limited to account-level token and cost totals — no projects or sessions.',
        source: 'Data from'
      };

  document.documentElement.lang = zh ? 'zh-CN' : 'en';
  document.title = text.title;

  byId('title').textContent = text.title;
  byId('subtitle').textContent = text.subtitle;
  byId('col-account').textContent = text.account;
  byId('col-tokens').textContent = text.tokenColumn;
  byId('col-cost').textContent = text.costColumn;
  byId('empty').textContent = text.empty;
  byId('footer-privacy').textContent = text.privacy;
  byId('footer-source').textContent = text.source;
  byId('origin').textContent = location.host;

  const tabs = Array.from(document.querySelectorAll('[data-board]'));
  tabs[0].textContent = text.cost;
  tabs[1].textContent = text.tokens;

  const rowsBody = byId('rows');
  const status = byId('status');
  const empty = byId('empty');
  const monthLabel = byId('month-label');
  const nextButton = byId('next-month');
  const prevButton = byId('prev-month');
  prevButton.setAttribute('aria-label', text.prev);
  nextButton.setAttribute('aria-label', text.next);
  const numberFormat = new Intl.NumberFormat(locale);
  const moneyFormat = new Intl.NumberFormat(locale, {
    style: 'currency',
    currency: 'USD',
    maximumFractionDigits: 2
  });

  const monthPattern = /^\d{4}-(0[1-9]|1[0-2])$/;
  const now = new Date();
  const thisMonth = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`;
  const params = new URLSearchParams(location.search);

  const state = {
    board: params.get('board') === 'tokens' ? 'tokens' : 'cost',
    month: monthPattern.test(params.get('month') || '') ? params.get('month') : thisMonth
  };

  function monthText(month) {
    const parts = month.split('-').map(Number);
    return new Date(parts[0], parts[1] - 1, 1).toLocaleDateString(locale, {
      year: 'numeric',
      month: 'long'
    });
  }

  function initial(name) {
    const chars = Array.from((name || '').trim());
    return chars.length > 0 ? chars[0].toUpperCase() : '?';
  }

  function updateUrl() {
    try {
      const url = new URL(location.href);
      url.searchParams.set('board', state.board);
      url.searchParams.set('month', state.month);
      history.replaceState(null, '', url);
    } catch {
      // 沙箱/只读环境下 URL 同步失败不影响榜单读取。
    }
  }

  function apiEndpoint() {
    // 相对页面路径解析：反代挂在 /relay 下时页面与 API 一起走前缀
    // （relay-common.js 的 resolve 已去掉尾部斜杠，/leaderboard/ 不会多解析一层）。
    return resolve('api/leaderboard');
  }

  function syncChrome() {
    monthLabel.textContent = monthText(state.month);
    nextButton.disabled = state.month >= thisMonth;
    for (const tab of tabs) {
      const active = tab.dataset.board === state.board;
      tab.setAttribute('aria-pressed', active ? 'true' : 'false');
    }
  }

  function cell(className, value) {
    const td = document.createElement('td');
    if (className) {
      td.className = className;
    }

    td.textContent = value;
    return td;
  }

  function render(data) {
    rowsBody.replaceChildren();
    const entries = Array.isArray(data.entries) ? data.entries : [];
    empty.hidden = entries.length !== 0;

    const participants = Number.isFinite(data.participants) ? data.participants : entries.length;
    status.textContent = `${numberFormat.format(participants)} ${
      participants === 1 ? text.participant : text.participants
    } · ${monthText(data.month || state.month)}`;

    for (const entry of entries) {
      const row = document.createElement('tr');

      const rank = cell('rank', String(entry.rank ?? ''));
      if ((entry.rank ?? 99) <= 3) {
        rank.classList.add('top');
      }

      const who = document.createElement('td');
      const wrap = document.createElement('div');
      wrap.className = 'who';

      const name = String(entry.displayName ?? 'anonymous');
      const avatar = document.createElement('span');
      avatar.className = 'avatar';
      if (typeof entry.avatarUrl === 'string' && /^https?:\/\//i.test(entry.avatarUrl)) {
        const image = document.createElement('img');
        image.src = entry.avatarUrl;
        image.alt = '';
        image.loading = 'lazy';
        image.decoding = 'async';
        image.referrerPolicy = 'no-referrer';
        image.addEventListener('error', () => {
          image.remove();
          avatar.textContent = initial(name);
        });
        avatar.appendChild(image);
      } else {
        avatar.textContent = initial(name);
      }

      const nameSpan = document.createElement('span');
      nameSpan.className = 'name';
      nameSpan.textContent = name;
      nameSpan.title = name;
      wrap.append(avatar, nameSpan);
      who.appendChild(wrap);

      row.append(
        rank,
        who,
        cell('num', numberFormat.format(Number(entry.totalTokens) || 0)),
        cell('num', moneyFormat.format(Number(entry.totalCostUsd) || 0))
      );
      rowsBody.appendChild(row);
    }
  }

  async function load() {
    syncChrome();
    updateUrl();
    empty.hidden = true;
    rowsBody.replaceChildren();
    status.textContent = text.loading;

    try {
      const query = new URLSearchParams({
        board: state.board,
        month: state.month,
        limit: '100'
      });
      const endpoint = apiEndpoint();
      endpoint.search = query.toString();
      const response = await fetch(endpoint, {
        headers: { Accept: 'application/json' }
      });
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      render(await response.json());
    } catch (error) {
      status.textContent = `${text.error} ${error && error.message ? error.message : ''}`.trim();
    }
  }

  function shiftMonth(delta) {
    const parts = state.month.split('-').map(Number);
    const date = new Date(parts[0], parts[1] - 1 + delta, 1);
    state.month = `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}`;
    load();
  }

  for (const tab of tabs) {
    tab.addEventListener('click', () => {
      state.board = tab.dataset.board;
      load();
    });
  }

  prevButton.addEventListener('click', () => shiftMonth(-1));
  nextButton.addEventListener('click', () => {
    if (state.month < thisMonth) {
      shiftMonth(1);
    }
  });

  load();
})();
