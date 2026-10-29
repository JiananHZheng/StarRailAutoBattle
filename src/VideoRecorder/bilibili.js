// Injected into the recorder's own WebView2 page (never the user's browser). It reads the player state, hides
// overlays (danmaku, controls, login prompts), clicks the player's own buttons, and can fetch parts of the video stream
// the player itself was given. It never logs in, fills a form, solves a verification, reads cookies or decrypts
// anything (logging in is done by the user in the login window; the page's own requests carry that session).
(() => {
  if (window.__srab) return;

  const HIDE = [
    '.bpx-player-row-dm-wrap', '.bpx-player-adv-dm-wrap', '.bpx-player-bas-dm-wrap', '.bpx-player-cmd-dm-wrap',
    '.bpx-player-dm-wrap', '.bpx-player-control-wrap', '.bpx-player-control-mask', '.bpx-player-top-wrap',
    '.bpx-player-state-wrap', '.bpx-player-toast-wrap', '.bpx-player-sending-area', '.bpx-player-dialog-wrap',
    '.bpx-player-ending-wrap', '.bpx-player-shadow-progress-area', '.bpx-player-tooltip-area',
    '.bpx-player-context-menu-wrap', '.bpx-player-electric-wrap', '.bili-mini-mask', '.login-tip',
  ];

  const style = () => {
    if (document.getElementById('srab-style')) return;
    const s = document.createElement('style');
    s.id = 'srab-style';
    s.textContent = HIDE.join(',') + '{display:none !important;}html,body{overflow:hidden !important;cursor:none !important;}' +
      // Fallback when the player's own web-fullscreen button does not work: pin the player to the window.
      '.srab-fill{position:fixed !important;left:0 !important;top:0 !important;width:100vw !important;height:100vh !important;' +
      'max-width:none !important;max-height:none !important;margin:0 !important;z-index:2147483646 !important;background:#000 !important;}' +
      '.srab-fill .bpx-player-primary-area,.srab-fill .bpx-player-video-area,.srab-fill .bpx-player-video-perch,' +
      '.srab-fill .bpx-player-video-wrap,.srab-fill video,.srab-fill bwp-video{width:100% !important;height:100% !important;' +
      'max-width:none !important;max-height:none !important;}';
    (document.head || document.documentElement).appendChild(s);
  };

  // The page may hold several media elements (hidden decoders, previews); take the one actually shown largest,
  // preferring one that has decoded a picture.
  const video = () => {
    let best = null, bestScore = -1;
    for (const v of document.querySelectorAll('video, bwp-video')) {
      const r = v.getBoundingClientRect();
      const score = Math.max(0, r.width) * Math.max(0, r.height) * ((v.videoWidth || 0) > 0 ? 2 : 1);
      if (score > bestScore) { best = v; bestScore = score; }
    }
    return best;
  };

  const captcha = () =>
    !!document.querySelector('.geetest_panel, .geetest_holder, .geetest_box, [class*="geetest_"], .bili-mini-captcha') ||
    /captcha|risk/i.test(location.pathname);

  const pageText = () => (document.body ? document.body.innerText.slice(0, 20000) : '');

  // The displayed picture inside the element (object-fit: contain), in CSS pixels of the viewport.
  const contentRect = v => {
    const r = v.getBoundingClientRect();
    let x = r.left, y = r.top, w = r.width, h = r.height;
    const vw = v.videoWidth || 0, vh = v.videoHeight || 0;
    if (vw > 0 && vh > 0 && w > 0 && h > 0) {
      const s = Math.min(w / vw, h / vh);
      x += (w - vw * s) / 2;
      y += (h - vh * s) / 2;
      w = vw * s;
      h = vh * s;
    }
    return { x, y, w, h };
  };

  // In web fullscreen the media element covers the whole window (the picture is letterboxed inside it). The normal
  // page layout can be as wide as a small window, so both directions must be covered.
  const fillsViewport = v => {
    const r = v.getBoundingClientRect();
    return r.left <= 2 && r.top <= 2 && r.width >= innerWidth * 0.97 && r.height >= innerHeight * 0.97;
  };

  const qualityItems = () => [...document.querySelectorAll('.bpx-player-ctrl-quality-menu-item')];
  const label = el => (el ? (el.textContent || '').replace(/\s+/g, ' ').trim() : '');

  window.__srab = {
    probe() {
      const v = video();
      return JSON.stringify({
        video: !!v,
        captcha: captcha(),
        notFound: !v && /视频不见了|稿件不可见|视频去哪了|该视频已失效|稿件已失效/.test(pageText()),
        rs: v ? v.readyState : 0,
        duration: v && isFinite(v.duration) ? v.duration : 0,
        url: location.href,
      });
    },

    // Danmaku off (it is also hidden by the style sheet).
    prepare() {
      style();
      const v = video();
      if (v) v.muted = true;

      const dm = document.querySelector('.bpx-player-dm-switch input[type="checkbox"]');
      if (dm && dm.checked) dm.click();
      return 'ok';
    },

    // Makes the picture fill the window: first the player's own web-fullscreen button (clicked once, since it
    // toggles), then pinning the player with the style sheet. The recorder calls this until it says 'filled'.
    fill() {
      style();
      const v = video();
      if (!v) return 'no-video';
      if (fillsViewport(v)) return 'filled';
      const web = document.querySelector('.bpx-player-ctrl-web');
      if (web && !window.__srabWebClicked) {
        window.__srabWebClicked = true;
        web.click();
        return 'clicked';
      }
      const box = v.closest('.bpx-player-container') || document.querySelector('#bilibili-player') || v.parentElement;
      if (box) box.classList.add('srab-fill');
      return 'pinned';
    },

    // Login state of the recorder's own profile, as Bilibili reports it (no user name or id is kept).
    // fetch is asynchronous, so the recorder starts it here and polls accountResult().
    accountStart() {
      window.__srabAccount = '';
      fetch('https://api.bilibili.com/x/web-interface/nav', { credentials: 'include' })
        .then(r => r.json())
        .then(j => {
          const d = (j && j.data) || {};
          window.__srabAccount = JSON.stringify({ login: !!d.isLogin, vip: !!d.isLogin && d.vipStatus === 1 });
        })
        .catch(() => { window.__srabAccount = JSON.stringify({ login: false, vip: false }); });
      return 'ok';
    },

    accountResult() {
      return window.__srabAccount || '';
    },

    // The DASH video streams offered to the player: the page's own play info, or else the same list from Bilibili's
    // play-URL API asked by the page (with this profile's session). The recorder polls streamsResult().
    streamsStart() {
      window.__srabStreams = '';
      const done = (d, source) => {
        const dash = d && d.dash;
        const list = (dash && dash.video) || [];
        const range = (v, a, b) => { const s = v.SegmentBase || v.segment_base || {}; return s[a] || s[b] || ''; };
        window.__srabStreams = JSON.stringify({
          source,
          drm: !!(d && (d.is_drm || d.drm_type)) || list.some(v => v.widevine_pssh || v.drm_type),
          duration: d && d.timelength ? d.timelength / 1000 : 0,
          accept: (d && d.accept_description) || [],
          videos: list.map(v => ({
            id: v.id || 0,
            codecs: v.codecs || '',
            width: v.width || 0,
            height: v.height || 0,
            fps: Number(v.frameRate || v.frame_rate) || 0,
            bandwidth: v.bandwidth || 0,
            urls: [v.baseUrl || v.base_url, ...(v.backupUrl || v.backup_url || [])].filter(Boolean),
            init: range(v, 'Initialization', 'initialization'),
            index: range(v, 'indexRange', 'index_range'),
          })),
        });
      };
      const p = window.__playinfo__;
      if (p && p.data && p.data.dash) { done(p.data, 'playinfo'); return 'ok'; }
      const s = window.__INITIAL_STATE__ || {};
      const bvid = s.bvid || (s.videoData && s.videoData.bvid);
      const cid = s.cid || (s.videoData && s.videoData.cid);
      if (!bvid || !cid) { done(null, 'none'); return 'ok'; }
      fetch(`https://api.bilibili.com/x/player/playurl?bvid=${encodeURIComponent(bvid)}&cid=${encodeURIComponent(cid)}&qn=80&fnval=4048&fourk=1`,
        { credentials: 'include' })
        .then(r => r.json())
        .then(j => done(j && j.code === 0 ? j.data : null, 'api'))
        .catch(() => done(null, 'api-error'));
      return 'ok';
    },

    streamsResult() {
      return window.__srabStreams || '';
    },

    // Fetches bytes from..to (inclusive) of a stream, trying each address in turn, as base64. Only a partial (206)
    // answer that starts at `from` and is as long as its Content-Range says is accepted; it may end before `to` (some
    // mirrors cut long answers), and the recorder asks again for the rest. The recorder polls fetchResult().
    fetchStart(urls, from, to) {
      window.__srabFetch = '';
      const put = o => { window.__srabFetch = JSON.stringify(o); };
      (async () => {
        let last = '';
        for (const u of urls) {
          try {
            const r = await fetch(u, { headers: { Range: `bytes=${from}-${to}` } });
            if (r.status !== 206) { last = 'HTTP ' + r.status; continue; }
            const m = /^bytes (\d+)-(\d+)\//.exec(r.headers.get('content-range') || '');
            if (!m || Number(m[1]) !== from || Number(m[2]) > to) { last = '返回的字节范围不对'; continue; }
            const blob = await r.blob();
            if (blob.size !== Number(m[2]) - from + 1) { last = '数据不完整'; continue; }
            const data = await new Promise((ok, fail) => {
              const f = new FileReader();
              f.onload = () => ok(String(f.result).split(',')[1] || '');
              f.onerror = () => fail(f.error);
              f.readAsDataURL(blob);
            });
            put({ ok: true, size: blob.size, data });
            return;
          } catch (e) {
            last = String((e && e.message) || e);
          }
        }
        put({ ok: false, size: 0, error: last });
      })();
      return 'ok';
    },

    fetchResult() {
      const r = window.__srabFetch || '';
      if (r) window.__srabFetch = '';
      return r;
    },

    // Highest quality this account may watch: 登录即享 items need a login, 大会员 items need 大会员.
    // The menu is built some time after the player, so the recorder calls this until options appear.
    selectQuality(allowLogin, allowVip) {
      const items = qualityItems();
      const free = items
        .filter(i => Number(i.getAttribute('data-value')) > 0)
        .filter(i => {
          const t = label(i);
          if (/大会员|会员/.test(t)) return allowVip;
          if (/登录/.test(t)) return allowLogin;
          return !/试看|试用/.test(t);
        })
        .sort((a, b) => Number(b.getAttribute('data-value')) - Number(a.getAttribute('data-value')));
      const best = free[0];
      if (best && !best.classList.contains('bpx-state-active')) best.click();

      return JSON.stringify({
        chosen: label(best),
        options: items.map(label),
      });
    },

    activeQuality() {
      return label(document.querySelector('.bpx-player-ctrl-quality-menu-item.bpx-state-active'));
    },

    seek(t) {
      const v = video();
      if (!v) return 'no-video';
      v.muted = true;
      v.currentTime = t;
      return 'ok';
    },

    // Slower playback when capturing cannot keep up with real time; status() re-applies it if the player resets it.
    rate(r) {
      window.__srabRate = r;
      const v = video();
      if (v) v.playbackRate = r;
      return 'ok';
    },

    play() {
      const v = video();
      if (!v) return 'no-video';
      v.muted = true;
      if (window.__srabRate && v.playbackRate !== window.__srabRate) v.playbackRate = window.__srabRate;
      const p = v.play();
      if (p && p.catch) p.catch(() => {});
      return 'ok';
    },

    pause() {
      const v = video();
      if (v) v.pause();
      return 'ok';
    },

    status() {
      style();
      const v = video();
      if (!v) return JSON.stringify({ video: false, captcha: captcha() });
      if (window.__srabRate && v.playbackRate !== window.__srabRate) v.playbackRate = window.__srabRate;
      const r = contentRect(v);
      return JSON.stringify({
        video: true,
        t: v.currentTime,
        paused: v.paused,
        ended: v.ended,
        seeking: v.seeking,
        rs: v.readyState,
        duration: isFinite(v.duration) ? v.duration : 0,
        vw: v.videoWidth || 0,
        vh: v.videoHeight || 0,
        x: r.x, y: r.y, w: r.w, h: r.h,
        iw: innerWidth,
        ih: innerHeight,
        dpr: devicePixelRatio,
        rate: v.playbackRate,
        media: document.querySelectorAll('video, bwp-video').length,
        login: !!document.querySelector('.bili-mini-mask, .login-panel-popover'),
        captcha: captcha(),
      });
    },

    meta() {
      const q = s => label(document.querySelector(s));
      const author = document.querySelector('meta[itemprop="author"], meta[name="author"]');
      const notice = pageText().match(/未经作者授权[^\n]{0,30}|禁止转载[^\n]{0,20}/);
      return JSON.stringify({
        title: q('h1.video-title') || q('.video-title') || document.title,
        uploader: q('.up-name') || q('.up-detail-top .up-name') || (author ? author.content : ''),
        notice: notice ? notice[0].trim() : '',
        url: location.href,
      });
    },
  };
})();
