// 中继自建网页（/admin、/leaderboard）的公共脚本。
// 两页的 CSP 已收紧到 script-src 'self'，不再有内联脚本：语言判定、页面基准地址与
// 同源 URL 解析统一放这里，页面各自的逻辑在 admin.js / leaderboard.js（均经 /assets 提供）。
(() => {
  'use strict';

  function byId(id) {
    return document.getElementById(id);
  }

  // 以页面地址为基准去掉 query/hash 与尾部斜杠：
  // 反代挂在 /relay 下时，/relay/admin 与 /relay/admin/ 都归一到 /relay/admin，
  // 再由 new URL(相对路径, base) 带上外部前缀（服务端看不到前缀，只有浏览器知道）。
  function pageBase() {
    const base = new URL(location.href);
    base.search = '';
    base.hash = '';
    if (base.pathname.endsWith('/')) {
      base.pathname = base.pathname.slice(0, -1) || '/';
    }

    return base;
  }

  // 相对页面路径解析：返回同源 URL（fetch 与 href 都直接接受 URL 对象）。
  function resolve(path) {
    return new URL(path, pageBase());
  }

  const language = (navigator.language || 'en').toLowerCase();
  const isZh = language.startsWith('zh');
  const locale = isZh ? 'zh-CN' : (navigator.language || 'en-US');

  window.TokenmetryRelay = Object.freeze({ byId, pageBase, resolve, isZh, locale });
})();
