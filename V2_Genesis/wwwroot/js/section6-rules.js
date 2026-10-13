/*
 * section6-rules.js
 * ---------------------------------------------------------------------------
 * ONE set of Section 6 rules for all four forms:
 *   Objection / Appeal            (ObjectionForm, ViewMultiPurposeForm)
 *   Section 78 Query / Review     (Section78Query, Section78QueryMulti)
 *
 * Load it LAST on the page (after every other form script).
 * Its listeners are on `window` in the capture phase, so they run before
 * the older Section 6 handlers in ObjectionJS / ObjectionMulti /
 * ObjectionQJS / ObjectionMulti_Section78 / Section78QueryParity /
 * section6-category-reset. When a rule fails, the older handlers are
 * stopped, so the client sees ONE clear message.
 *
 * 1. CATEGORY (6.2)
 *    The new category may not be the category that is already on the roll.
 *    Roll names and dropdown names are matched, e.g.
 *       "Residential"            = "Residential Property"
 *       "Municipal"              = "Municipal Property"
 *       "Mining"                 = "Mining Land"
 *       "Farming"                = "Agricultural"
 *       "Public Service Purposes"= "Public Service Purpose"
 *       "Consent Use"            = "Residential with Consent Use"
 *       "Township Establishment" = "Township Development"
 *       "Multiple Purposes"      = "MultiPurpose*"
 *    The dropdowns now use the Rates Policy 2026/27 names (Helpers/RollCategories.cs);
 *    the old names above stay here so roll values still match.
 *    SPLIT categories belong to a multipurpose property. When the roll
 *    shows "Split - X" the client may NOT choose:
 *       - "MultiPurpose*"  (a split is already multipurpose), or
 *       - X itself         (e.g. "Split - Public Service Infrastructure"
 *                           -> "Public Service Infrastructure" not allowed).
 *    Any other category is allowed. An empty roll category (NULL) is never
 *    compared.
 *
 * 2. NEXT on Section 6
 *    a) Nothing filled in on the right (6.2) -> "Section 6 is not complete"
 *    b) A category that is not allowed       -> "Category not allowed"
 *    c) A value that is the same as the roll -> "Same as the roll"
 * ---------------------------------------------------------------------------
 */
(function () {
    'use strict';

    if (window.__genesisSection6Rules) return;
    window.__genesisSection6Rules = true;

    // ════════════════════════════════════════════════════════════════
    // CATEGORY NAMES
    // ════════════════════════════════════════════════════════════════
    var CATEGORY_KEYS = {
        'multiple purposes': 'multipurpose',
        'multiple purpose': 'multipurpose',
        'multipurpose': 'multipurpose',
        'multipurposes': 'multipurpose',
        'multi purpose': 'multipurpose',
        'multi-purpose': 'multipurpose',
        'mixed use': 'multipurpose',

        'residential': 'residential',
        'residential property': 'residential',

        'residential with consent use': 'residential-consent',
        'residential consent use': 'residential-consent',
        'consent use': 'residential-consent',

        'business': 'business',
        'commercial': 'business',
        'business and commercial': 'business',
        'business & commercial': 'business',

        'agricultural': 'agricultural',
        'agriculture': 'agricultural',
        'agric': 'agricultural',
        'farming': 'agricultural',
        'farm': 'agricultural',

        'industrial': 'industrial',

        'mining': 'mining',
        'mining land': 'mining',
        'mining property': 'mining',

        'municipal': 'municipal',
        'municipal property': 'municipal',

        'private open space': 'private-open-space',
        'public open space': 'public-open-space',

        'public service infrastructure': 'psi',
        'public service infrastructure-private': 'psi-private',
        'public service infrastructure private': 'psi-private',
        'public service infrastructure - private': 'psi-private',
        'private service infrastructure': 'psi-private',

        'public service purpose': 'psp',
        'public service purposes': 'psp',

        'public benefit organisation': 'pbo',
        'public benefit organization': 'pbo',
        'public benefit organisations': 'pbo',

        'religious': 'religious',

        'township development': 'township',
        'township establishment': 'township',

        'vacant land': 'vacant',
        'vacant': 'vacant'
    };

    function cleanCategory(value) {
        return String(value == null ? '' : value)
            .replace(/\*/g, '')
            .replace(/ /g, ' ')
            .trim()
            .toLowerCase()
            .replace(/\s*-\s*/g, '-')        // "A - B" / "A -B" -> "A-B"
            .replace(/\s+/g, ' ')
            .replace(/[.,;:]+$/g, '');
    }

    function categoryKey(value) {
        var v = cleanCategory(value);
        if (!v || v === 'null' || v === 'n/a' || v === '-' || v === '–') return '';
        return CATEGORY_KEYS[v] || v;
    }

    // Everything the roll category already "is".
    function rollCategoryKeys(rollValue) {
        var v = cleanCategory(rollValue);
        if (!v || v === 'null') return [];

        var split = v.match(/^split-(.+)$/);
        if (split) {
            var inner = categoryKey(split[1]);
            return inner ? ['multipurpose', inner] : ['multipurpose'];
        }

        var key = categoryKey(v);
        return key ? [key] : [];
    }

    function isSplit(rollValue) {
        return /^split-/.test(cleanCategory(rollValue));
    }

    // null = allowed; otherwise the reason it is not allowed.
    function categoryProblem(selectedValue, rollValue) {
        var chosen = categoryKey(selectedValue);
        if (!chosen) return null;

        var keys = rollCategoryKeys(rollValue);
        if (keys.indexOf(chosen) === -1) return null;

        var roll = String(rollValue || '').trim();
        var picked = String(selectedValue || '').trim();

        if (isSplit(rollValue)) {
            return chosen === 'multipurpose'
                ? 'The roll category is "' + roll + '". A split is already part of a multipurpose property, so "' + picked + '" is not a change. Choose a different category, or leave Category blank.'
                : 'The roll category is "' + roll + '". This part of the multipurpose property is already "' + picked + '", so that is not a change. Choose a different category, or leave Category blank.';
        }

        return 'The roll category is already "' + roll + '", so "' + picked + '" is not a change. Choose a different category, or leave Category blank.';
    }

    window.GenesisCategory = {
        key: categoryKey,
        rollKeys: rollCategoryKeys,
        problem: categoryProblem,
        isSame: function (selected, roll) { return categoryProblem(selected, roll) !== null; }
    };

    // ════════════════════════════════════════════════════════════════
    // SECTION 6 FIELDS
    // ════════════════════════════════════════════════════════════════
    var PAIRS = [
        { label: 'Description of the Property / Unit', newId: 'NewPropDesc', oldId: 'desc', type: 'text' },
        { label: 'Category', newId: 'NewCat', oldId: 'cat', type: 'category' },
        { label: 'Physical Address / Door No. / Flat No.', newId: 'NewAddress', oldId: 'add', type: 'text' },
        { label: 'Extent', newId: 'NewExtent', oldId: 'extent', type: 'extent' },
        { label: 'Market Value', newId: 'NewMarketValue', oldId: 'Market_Value', type: 'market' },
        { label: 'Name of Owner', newId: 'NewOwner', oldId: 'owner', type: 'text' },
        { label: 'Purpose 2 Category', newId: 'NewCat1', oldId: 'cat1', type: 'category' },
        { label: 'Purpose 2 Extent', newId: 'NewExtent1', oldId: 'extent1', type: 'extent' },
        { label: 'Purpose 2 Market Value', newId: 'NewMarketValue1', oldId: 'Market_Value1', type: 'market' },
        { label: 'Purpose 3 Category', newId: 'NewCat2', oldId: 'cat2', type: 'category' },
        { label: 'Purpose 3 Extent', newId: 'NewExtent2', oldId: 'extent2', type: 'extent' },
        { label: 'Purpose 3 Market Value', newId: 'NewMarketValue2', oldId: 'Market_Value2', type: 'market' },
        { label: 'Purpose 4 Category', newId: 'NewCat3', oldId: 'cat3', type: 'category' },
        { label: 'Purpose 4 Extent', newId: 'NewExtent3', oldId: 'extent3', type: 'extent' },
        { label: 'Purpose 4 Market Value', newId: 'NewMarketValue3', oldId: 'Market_Value3', type: 'market' }
    ];

    var CATEGORY_IDS = ['NewCat', 'NewCat1', 'NewCat2', 'NewCat3'];

    function el(id) { return document.getElementById(id); }

    // Only fields that are really on this form and in Section 6.
    function activePairs() {
        var card = document.querySelector('#myForm .div6') || document;
        return PAIRS.filter(function (p) {
            var n = el(p.newId);
            return n && card.contains(n);
        });
    }

    function hasValue(input) {
        if (!input) return false;
        return String(input.value || '').trim() !== '';
    }

    function textKey(v) {
        return String(v == null ? '' : v).trim().toLowerCase().replace(/\s+/g, ' ').replace(/[.,;:]+$/g, '');
    }

    function numberKey(v) {
        var cleaned = String(v == null ? '' : v).replace(/R/gi, '').replace(/[\s  ,]/g, '').trim();
        if (!cleaned) return '';
        var n = Number(cleaned);
        return isNaN(n) ? cleaned.toLowerCase() : String(n);
    }

    function extentKey(v) {
        var cleaned = String(v == null ? '' : v).replace(/\s/g, '').replace(/,/g, '.').trim();
        if (!cleaned) return '';
        var n = Number(cleaned);
        return isNaN(n) ? cleaned.toLowerCase() : String(n);
    }

    function sameAsRoll(pair) {
        var n = el(pair.newId), o = el(pair.oldId);
        if (!n || !o || !hasValue(n)) return false;
        if (pair.type === 'category') return false; // handled by categoryProblem
        var a, b;
        if (pair.type === 'market') { a = numberKey(n.value); b = numberKey(o.value); }
        else if (pair.type === 'extent') { a = extentKey(n.value); b = extentKey(o.value); }
        else { a = textKey(n.value); b = textKey(o.value); }
        return a !== '' && b !== '' && a === b;
    }

    function mode() {
        var review = el('reviewStat');
        if (review) {
            var r = String(review.value || '').trim().toLowerCase();
            return r === 'r' || r === 'review' ? 'review' : 'query';
        }
        var appeal = el('AppealStat');
        var a = appeal ? String(appeal.value || '').toLowerCase() : '';
        var s = '';
        try { s = String(sessionStorage.getItem('AppealStatus') || '').toLowerCase(); } catch (e) { }
        return a === 'true' || s === 'true' ? 'appeal' : 'objection';
    }

    // ════════════════════════════════════════════════════════════════
    // MESSAGES
    // ════════════════════════════════════════════════════════════════
    function injectStyles() {
        if (el('gs6-styles')) return;
        var css = document.createElement('style');
        css.id = 'gs6-styles';
        css.textContent =
            '.gs6-backdrop{position:fixed;inset:0;z-index:10060;display:none;align-items:center;justify-content:center;padding:18px;background:rgba(17,24,39,.6)}' +
            '.gs6-modal{width:min(580px,100%);max-height:calc(100vh - 36px);overflow:auto;background:#fff;border-radius:16px;box-shadow:0 24px 70px rgba(0,0,0,.3);font-family:Poppins,sans-serif;border-top:6px solid #b91c1c}' +
            '.gs6-modal.gs6-info{border-top-color:#e6b000}' +
            '.gs6-head{display:flex;gap:14px;align-items:center;padding:20px 22px 14px}' +
            '.gs6-icon{flex:0 0 46px;width:46px;height:46px;border-radius:50%;display:flex;align-items:center;justify-content:center;background:#fee2e2;color:#b91c1c;font-size:20px}' +
            '.gs6-info .gs6-icon{background:#fff4cc;color:#8a6500}' +
            '.gs6-head h3{margin:0;color:#111827;font-size:19px;font-weight:800;line-height:1.25}' +
            '.gs6-head p{margin:3px 0 0;color:#4b5563;font-size:13px}' +
            '.gs6-body{padding:4px 22px 6px;color:#1f2937;font-size:15px;line-height:1.6}' +
            '.gs6-body ul{margin:10px 0 4px;padding-left:20px}.gs6-body li{margin:0 0 6px}' +
            '.gs6-tip{margin:12px 0 4px;padding:10px 12px;border-radius:10px;background:#f0fdfa;border-left:4px solid #006570;color:#134e4a;font-size:14px}' +
            '.gs6-foot{display:flex;justify-content:flex-end;padding:14px 22px 20px}' +
            '.gs6-btn{border:0;border-radius:10px;background:#006570;color:#fff;padding:11px 20px;font-weight:700;font-size:15px;cursor:pointer}' +
            '.gs6-btn:hover{background:#004f58}' +
            '.gs6-field-msg{display:block;margin:-8px 0 12px;color:#b91c1c;font-size:13px;font-weight:600;line-height:1.4}' +
            '.gs6-bad{border:2px solid #b91c1c !important;background:#fff1f1 !important}' +
            '.gs6-empty{border:2px dashed #b91c1c !important}' +
            '#new_change_invalid.gs6-show{display:block;margin:10px 0 0;padding:10px 12px;border-radius:10px;background:#fef2f2;border:1px solid #fecaca;color:#991b1b !important;font-weight:600;font-size:14px}';
        document.head.appendChild(css);
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function showModal(opts) {
        injectStyles();
        var back = el('gs6Modal');
        if (!back) {
            back = document.createElement('div');
            back.id = 'gs6Modal';
            back.className = 'gs6-backdrop';
            back.innerHTML =
                '<div class="gs6-modal" role="dialog" aria-modal="true" aria-labelledby="gs6Title">' +
                '<div class="gs6-head"><div class="gs6-icon"><i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i></div>' +
                '<div><h3 id="gs6Title"></h3><p id="gs6Sub"></p></div></div>' +
                '<div class="gs6-body" id="gs6Body"></div>' +
                '<div class="gs6-foot"><button type="button" class="gs6-btn" id="gs6Close">OK</button></div>' +
                '</div>';
            document.body.appendChild(back);
            back.addEventListener('click', function (e) { if (e.target === back) closeModal(); });
            el('gs6Close').addEventListener('click', closeModal);
            document.addEventListener('keydown', function (e) {
                if (e.key === 'Escape' && back.style.display === 'flex') closeModal();
            });
        }
        back.querySelector('.gs6-modal').classList.toggle('gs6-info', !!opts.info);
        el('gs6Title').textContent = opts.title;
        el('gs6Sub').textContent = opts.sub || '';
        el('gs6Body').innerHTML = opts.html;
        el('gs6Close').textContent = opts.button || 'OK';
        back.style.display = 'flex';
        document.body.style.overflow = 'hidden';
        setTimeout(function () { el('gs6Close').focus({ preventScroll: true }); }, 30);
        back._afterClose = opts.afterClose || null;
    }

    function closeModal() {
        var back = el('gs6Modal');
        if (!back) return;
        back.style.display = 'none';
        document.body.style.overflow = '';
        var after = back._afterClose;
        back._afterClose = null;
        if (typeof after === 'function') after();
    }

    function setFieldMessage(input, text) {
        if (!input) return;
        injectStyles();
        input.classList.add('gs6-bad');
        var id = input.id + '_gs6_msg';
        var msg = el(id);
        if (!msg) {
            msg = document.createElement('span');
            msg.id = id;
            msg.className = 'gs6-field-msg';
            msg.setAttribute('role', 'alert');
            input.parentNode.insertBefore(msg, input.nextSibling);
        }
        msg.textContent = text;
    }

    function clearFieldMessage(input) {
        if (!input) return;
        input.classList.remove('gs6-bad', 'gs6-empty');
        input.style.border = '';
        input.style.backgroundColor = '';
        var msg = el(input.id + '_gs6_msg');
        if (msg) msg.remove();
        var legacy = el(input.id + '_same_error');
        if (legacy) legacy.remove();
    }

    function setSummary(text) {
        var box = el('new_change_invalid');
        if (!box) return;
        injectStyles();
        if (text) {
            box.textContent = text;
            box.classList.add('gs6-show');
        } else {
            box.textContent = '';
            box.classList.remove('gs6-show');
        }
        box.style.color = '';
    }

    function resetSelect(select) {
        if (!select) return;
        select.value = '';
        if (select.value !== '') select.selectedIndex = 0;
    }

    function stop(e) {
        e.preventDefault();
        e.stopPropagation();
        e.stopImmediatePropagation();
    }

    // ════════════════════════════════════════════════════════════════
    // 1. CATEGORY CHANGE
    // ════════════════════════════════════════════════════════════════
    function rollFor(selectId) {
        var pair = PAIRS.filter(function (p) { return p.newId === selectId; })[0];
        return pair ? el(pair.oldId) : null;
    }

    window.addEventListener('change', function (e) {
        var select = e.target;
        if (!select || CATEGORY_IDS.indexOf(select.id) === -1) return;

        var roll = rollFor(select.id);
        clearFieldMessage(select);
        if (!roll) return;

        var problem = categoryProblem(select.value, roll.value);
        if (!problem) return;

        stop(e);
        var picked = select.options[select.selectedIndex] ? select.options[select.selectedIndex].text : select.value;
        resetSelect(select);
        setFieldMessage(select, 'Not allowed: ' + problem);

        showModal({
            title: 'This category is not allowed',
            sub: 'Section 6.2 – Category',
            html:
                '<p>You chose <strong>' + esc(picked) + '</strong>.</p>' +
                '<p>' + esc(problem) + '</p>' +
                '<div class="gs6-tip">The Category box has been cleared. You only need to fill in the details you want the City to change.</div>',
            button: 'OK, I understand',
            afterClose: function () { select.focus({ preventScroll: false }); }
        });
    }, true);

    // ════════════════════════════════════════════════════════════════
    // 2. NEXT ON SECTION 6
    // ════════════════════════════════════════════════════════════════
    window.addEventListener('click', function (e) {
        var btn = e.target && e.target.closest ? e.target.closest('#myForm .btn_n6') : null;
        if (!btn) return;

        var pairs = activePairs();
        if (!pairs.length) return;

        pairs.forEach(function (p) { clearFieldMessage(el(p.newId)); });
        setSummary('');

        var word = mode(); // objection / appeal / query / review

        // a) Nothing filled in on the right.
        var filled = pairs.filter(function (p) { return hasValue(el(p.newId)); });
        if (!filled.length) {
            stop(e);
            pairs.forEach(function (p) { var n = el(p.newId); if (n) n.classList.add('gs6-empty'); });
            setSummary('Section 6 is not complete: fill in at least one new detail in the right column (6.2) before you continue.');

            showModal({
                title: 'Section 6 is not complete',
                sub: 'Tell us what you want the City to change',
                info: true,
                html:
                    '<p>You have not filled in any new details in the <strong>right column (6.2)</strong>.</p>' +
                    '<p>To lodge this ' + word + ', fill in <strong>at least one</strong> detail you want the City to change, for example:</p>' +
                    '<ul>' +
                    '<li>the <strong>Market Value</strong> you think is correct,</li>' +
                    '<li>a different <strong>Category</strong>,</li>' +
                    '<li>the correct <strong>Extent</strong>, description, address or owner.</li>' +
                    '</ul>' +
                    '<div class="gs6-tip">Leave the boxes you do not want to change blank. The left column (6.1) shows what is on the roll and cannot be changed.</div>',
                button: 'OK, I will fill it in',
                afterClose: function () {
                    var first = el(pairs[0].newId);
                    var mv = pairs.filter(function (p) { return p.newId === 'NewMarketValue'; })[0];
                    var target = mv ? el(mv.newId) : first;
                    if (target) target.focus({ preventScroll: false });
                }
            });
            return;
        }

        // b) Category that is not allowed (also catches a restored draft).
        var badCategories = [];
        pairs.forEach(function (p) {
            if (p.type !== 'category') return;
            var n = el(p.newId), o = el(p.oldId);
            if (!n || !o || !hasValue(n)) return;
            var problem = categoryProblem(n.value, o.value);
            if (problem) badCategories.push({ pair: p, problem: problem, picked: n.options[n.selectedIndex] ? n.options[n.selectedIndex].text : n.value });
        });

        // c) Values that are the same as the roll.
        var same = pairs.filter(sameAsRoll);

        if (badCategories.length || same.length) {
            stop(e);

            var items = [];
            badCategories.forEach(function (b) {
                var n = el(b.pair.newId);
                resetSelect(n);
                setFieldMessage(n, 'Not allowed: ' + b.problem);
                items.push('<li><strong>' + esc(b.pair.label) + ':</strong> ' + esc(b.problem) + ' <em>(cleared)</em></li>');
            });
            same.forEach(function (p) {
                var n = el(p.newId);
                setFieldMessage(n, 'This is the same as the roll. Enter a different value, or clear this box.');
                items.push('<li><strong>' + esc(p.label) + ':</strong> the value you entered is the same as on the roll.</li>');
            });

            // Only categories were wrong and they were cleared: is anything left?
            var stillFilled = pairs.filter(function (p) { return hasValue(el(p.newId)); });

            setSummary('Some details in Section 6.2 need your attention. They are marked in red.');

            showModal({
                title: 'Please check Section 6.2',
                sub: 'A new value must be different from the value on the roll',
                html:
                    '<ul>' + items.join('') + '</ul>' +
                    (stillFilled.length
                        ? '<div class="gs6-tip">Fix or clear the boxes marked in red, then click Next again.</div>'
                        : '<div class="gs6-tip">After this, nothing is left in the right column. Fill in at least one detail you want the City to change.</div>'),
                button: 'OK, I will fix it',
                afterClose: function () {
                    var first = el((same[0] || (badCategories[0] && badCategories[0].pair) || pairs[0]).newId);
                    if (first) first.focus({ preventScroll: false });
                }
            });
            return;
        }

        // All good: let the form's own Next handler continue.
    }, true);

    // Clear the red marks as soon as the client types / chooses.
    window.addEventListener('input', function (e) {
        var t = e.target;
        if (!t || !t.id) return;
        if (PAIRS.some(function (p) { return p.newId === t.id; })) {
            clearFieldMessage(t);
            if (hasValue(t)) {
                PAIRS.forEach(function (p) { var n = el(p.newId); if (n) n.classList.remove('gs6-empty'); });
                setSummary('');
            }
        }
    }, true);
})();
