// Fills elements marked data-i18n / data-i18n-placeholder with localized text (no innerHTML).
'use strict';
document.querySelectorAll('[data-i18n]').forEach((el) => { el.textContent = chrome.i18n.getMessage(el.dataset.i18n) || el.dataset.i18n; });
document.querySelectorAll('[data-i18n-placeholder]').forEach((el) => { el.placeholder = chrome.i18n.getMessage(el.dataset.i18nPlaceholder) || ''; });
document.documentElement.lang = chrome.i18n.getMessage('@@ui_locale').split('_')[0] || 'en';
