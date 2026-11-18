(function () {
  const REPO = "kAdimiron/gem";
  const RELEASES_PAGE = `https://github.com/${REPO}/releases/latest`;
  const RTL_PREFIXES = new Set(["ar", "fa", "he", "ur"]);
  const STORAGE_LANG_KEY = "gem_site_lang";
  const STORAGE_THEME_KEY = "gem_site_theme";

  const i18n = window.GEM_I18N || { languages: [], strings: {} };
  const langSelect = document.getElementById("langSelect");
  const themeBtn = document.getElementById("themeToggle");
  const sunIcon = document.getElementById("sunIcon");
  const moonIcon = document.getElementById("moonIcon");
  const installerBtn = document.getElementById("btnInstaller");
  const portableBtn = document.getElementById("btnPortable");
  const installerMeta = document.getElementById("installerMeta");
  const portableMeta = document.getElementById("portableMeta");
  const versionPill = document.getElementById("versionPill");

  function formatBytes(bytes) {
    if (!bytes || bytes <= 0) return "";
    const units = ["B", "KB", "MB", "GB"];
    let val = bytes;
    let idx = 0;
    while (val >= 1024 && idx < units.length - 1) {
      val /= 1024;
      idx++;
    }
    return `${val.toFixed(1)} ${units[idx]}`;
  }

  function resolveInitialLanguage() {
    const supported = new Set(i18n.languages.map((l) => l.code));
    const lowerMap = new Map(i18n.languages.map((l) => [l.code.toLowerCase(), l.code]));

    const params = new URLSearchParams(window.location.search);
    const paramLang = params.get("lang");
    if (paramLang && lowerMap.has(paramLang.toLowerCase())) {
      return lowerMap.get(paramLang.toLowerCase());
    }

    const saved = localStorage.getItem(STORAGE_LANG_KEY);
    if (saved && supported.has(saved)) {
      return saved;
    }

    const browserPrefs = navigator.languages && navigator.languages.length
      ? navigator.languages
      : [navigator.language || "en-US"];

    const specialAliases = {
      "zh-hans": "zh-CN",
      "zh-sg": "zh-CN",
      "zh-hant": "zh-TW",
      "zh-mo": "zh-HK",
      "zh": "zh-CN",
      "pt": "pt-BR",
      "es-419": "es-MX",
      "no": "nb-NO",
      "nb": "nb-NO",
      "nn": "nn-NO",
      "sr-cyrl": "sr-Cyrl-RS",
      "sr-latn": "sr-Latn-RS",
      "sr": "sr-Cyrl-RS",
      "az": "az-Latn-AZ",
      "uz": "uz-Latn-UZ",
      "bs": "bs-Latn-BA",
      "tg": "tg-Cyrl-TJ",
      "tl": "fil-PH"
    };

    for (const raw of browserPrefs) {
      if (!raw) continue;
      const lower = raw.toLowerCase();
      if (lowerMap.has(lower)) return lowerMap.get(lower);

      for (const [prefix, mapped] of Object.entries(specialAliases)) {
        if (lower === prefix || lower.startsWith(prefix + "-")) {
          if (supported.has(mapped)) return mapped;
        }
      }

      const base = lower.split("-")[0];
      const byBase = i18n.languages.find((l) => l.code.split("-")[0].toLowerCase() === base);
      if (byBase) return byBase.code;
    }

    return "en-US";
  }

  function applyLanguage(code) {
    const dict = i18n.strings[code] || i18n.strings["en-US"];
    if (!dict) return;

    localStorage.setItem(STORAGE_LANG_KEY, code);
    langSelect.value = code;

    const base = code.split("-")[0].toLowerCase();
    document.documentElement.lang = code;
    document.documentElement.dir = RTL_PREFIXES.has(base) ? "rtl" : "ltr";

    document.querySelectorAll("[data-i18n]").forEach((el) => {
      const key = el.getAttribute("data-i18n");
      if (dict[key] !== undefined) {
        el.textContent = dict[key];
      }
    });

    const isLight = document.documentElement.getAttribute("data-theme") === "light";
    themeBtn.title = isLight ? dict.ThemeToggleToDark : dict.ThemeToggleToLight;
    document.title = `Gem — ${dict.WebHeroTitle}`;
  }

  function applyTheme(theme) {
    document.documentElement.setAttribute("data-theme", theme);
    localStorage.setItem(STORAGE_THEME_KEY, theme);
    const isLight = theme === "light";
    sunIcon.style.display = isLight ? "none" : "block";
    moonIcon.style.display = isLight ? "block" : "none";

    const dict = i18n.strings[langSelect.value] || i18n.strings["en-US"];
    if (dict) {
      themeBtn.title = isLight ? dict.ThemeToggleToDark : dict.ThemeToggleToLight;
    }
  }

  // Populate 86 languages
  for (const lang of i18n.languages) {
    const opt = document.createElement("option");
    opt.value = lang.code;
    opt.textContent = `${lang.nativeName} (${lang.code})`;
    langSelect.appendChild(opt);
  }

  const initialTheme = localStorage.getItem(STORAGE_THEME_KEY) || "dark";
  applyTheme(initialTheme);
  applyLanguage(resolveInitialLanguage());

  langSelect.addEventListener("change", (e) => applyLanguage(e.target.value));
  themeBtn.addEventListener("click", () => {
    const next = document.documentElement.getAttribute("data-theme") === "light" ? "dark" : "light";
    applyTheme(next);
  });

  // Lightbox
  const lightbox = document.getElementById("lightbox");
  const lightboxImg = document.getElementById("lightboxImg");
  const lightboxClose = document.getElementById("lightboxClose");

  document.querySelectorAll("[data-lightbox]").forEach((trigger) => {
    trigger.addEventListener("click", () => {
      const img = trigger.querySelector("img");
      if (!img) return;
      lightboxImg.src = img.src;
      lightboxImg.alt = img.alt;
      lightbox.classList.add("open");
    });
  });

  function closeLightbox() {
    lightbox.classList.remove("open");
    lightboxImg.src = "";
  }

  lightboxClose.addEventListener("click", closeLightbox);
  lightbox.addEventListener("click", (e) => {
    if (e.target === lightbox) closeLightbox();
  });
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape") closeLightbox();
  });

  // Load latest release download URLs from GitHub Releases API
  installerBtn.href = RELEASES_PAGE;
  portableBtn.href = RELEASES_PAGE;

  fetch(`https://api.github.com/repos/${REPO}/releases/latest`)
    .then((res) => (res.ok ? res.json() : null))
    .then((release) => {
      if (!release || !Array.isArray(release.assets)) return;

      if (release.tag_name) {
        versionPill.textContent = release.tag_name.startsWith("v")
          ? release.tag_name
          : `v${release.tag_name}`;
      }

      const setupAsset = release.assets.find((a) =>
        /setup|install/i.test(a.name) && a.name.toLowerCase().endsWith(".exe")
      );
      const portableAsset = release.assets.find((a) =>
        !/setup|install/i.test(a.name) && a.name.toLowerCase().endsWith(".exe")
      );

      if (setupAsset) {
        installerBtn.href = setupAsset.browser_download_url;
        installerMeta.textContent = formatBytes(setupAsset.size);
      } else if (portableAsset) {
        installerBtn.href = portableAsset.browser_download_url;
      }

      if (portableAsset) {
        portableBtn.href = portableAsset.browser_download_url;
        portableMeta.textContent = formatBytes(portableAsset.size);
      }
    })
    .catch(() => {
      // Fallback links already point to /releases/latest
    });
})();
