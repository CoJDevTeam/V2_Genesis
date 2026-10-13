// Section 78 Query/Review form parity with the Objection forms.
// Keeps the same-value modal behaviour and resets category dropdowns
// when the selected category is already reflected on the valuation roll.
(function () {
    "use strict";

    if (window.__section78QueryParityLoaded) return;
    window.__section78QueryParityLoaded = true;

    function modeName() {
        var review = document.getElementById("reviewStat");
        var value = review ? String(review.value || "").trim().toLowerCase() : "";
        return value === "r" || value === "review" ? "Review" : "Query";
    }

    function normaliseText(value) {
        return String(value || "")
            .trim()
            .toLowerCase()
            .replace(/\s+/g, " ")
            .replace(/[.,;:]+$/g, "");
    }

    function normaliseCategory(value) {
        var v = normaliseText(value);
        var map = {
            "residential": "residential",
            "residential property": "residential",
            "business": "business and commercial",
            "commercial": "business and commercial",
            "business and commercial": "business and commercial",
            "farming": "agricultural",
            "agric": "agricultural",
            "agriculture": "agricultural",
            "agricultural": "agricultural",
            "multiple purposes": "multipurpose",
            "multipurpose": "multipurpose",
            "multipurpose*": "multipurpose",
            "multi purpose": "multipurpose",
            "multi-purpose": "multipurpose",
            "vacant": "vacant land",
            "vacant land": "vacant land",
            // Rates Policy 2026/27 names (Helpers/RollCategories.cs) vs old names
            "mining land": "mining",
            "municipal property": "municipal",
            "public service purpose": "public service purposes",
            "public benefit organisations": "public benefit organisation",
            "public service infrastructure - private": "private service infrastructure",
            "public service infrastructure -private": "private service infrastructure",
            "public service infrastructure private": "private service infrastructure",
            "consent use": "residential with consent use",
            "township establishment": "township development"
        };
        return map[v] || v;
    }

    function normaliseNumber(value) {
        var cleaned = String(value || "")
            .replace(/R/gi, "")
            .replace(/[\s,]/g, "")
            .trim();
        if (!cleaned) return "";
        var num = Number(cleaned);
        return Number.isNaN(num) ? cleaned.toLowerCase() : String(num);
    }

    function normaliseExtent(value) {
        var cleaned = String(value || "")
            .replace(/\s/g, "")
            .replace(/,/g, ".")
            .trim();
        if (!cleaned) return "";
        var num = Number(cleaned);
        return Number.isNaN(num) ? cleaned.toLowerCase() : String(num);
    }

    function normalise(value, type) {
        if (type === "category") return normaliseCategory(value);
        if (type === "market") return normaliseNumber(value);
        if (type === "extent") return normaliseExtent(value);
        return normaliseText(value);
    }

    function pairs() {
        return [
            { label: "Description of the Property/Unit", newId: "NewPropDesc", oldId: "desc", type: "text" },
            { label: "Category", newId: "NewCat", oldId: "cat", type: "category" },
            { label: "Physical Address / Door No. / Flat No.", newId: "NewAddress", oldId: "add", type: "text" },
            { label: "Extent", newId: "NewExtent", oldId: "extent", type: "extent" },
            { label: "Market Value", newId: "NewMarketValue", oldId: "Market_Value", type: "market" },
            { label: "Name of Owner", newId: "NewOwner", oldId: "owner", type: "text" },
            { label: "Purpose 2 Category", newId: "NewCat1", oldId: "cat1", type: "category" },
            { label: "Purpose 2 Extent", newId: "NewExtent1", oldId: "extent1", type: "extent" },
            { label: "Purpose 2 Market Value", newId: "NewMarketValue1", oldId: "Market_Value1", type: "market" },
            { label: "Purpose 3 Category", newId: "NewCat2", oldId: "cat2", type: "category" },
            { label: "Purpose 3 Extent", newId: "NewExtent2", oldId: "extent2", type: "extent" },
            { label: "Purpose 3 Market Value", newId: "NewMarketValue2", oldId: "Market_Value2", type: "market" },
            { label: "Purpose 4 Category", newId: "NewCat3", oldId: "cat3", type: "category" },
            { label: "Purpose 4 Extent", newId: "NewExtent3", oldId: "extent3", type: "extent" },
            { label: "Purpose 4 Market Value", newId: "NewMarketValue3", oldId: "Market_Value3", type: "market" }
        ];
    }

    function setError(input) {
        if (!input) return;
        input.style.border = "2px solid #d00000";
        input.style.backgroundColor = "#fff1f1";
    }

    function clearError(input) {
        if (!input) return;
        input.style.border = "";
        input.style.backgroundColor = "";
    }

    function resetCategory(select) {
        if (!select) return;
        select.value = "";
        select.selectedIndex = 0;
        setTimeout(function () { select.focus(); }, 100);
    }

    function ensureModal() {
        if (document.getElementById("section78SameValueModal")) return;

        var style = document.createElement("style");
        style.textContent = `
            .s78-modal-backdrop{position:fixed;inset:0;z-index:10050;background:rgba(17,24,39,.62);display:none;align-items:center;justify-content:center;padding:20px}
            .s78-modal{width:min(560px,100%);background:#fff;border-radius:14px;overflow:hidden;box-shadow:0 24px 70px rgba(0,0,0,.28);font-family:'Poppins',sans-serif;border-top:5px solid #e6b000}
            .s78-modal-head{display:flex;gap:14px;align-items:center;padding:20px 22px;background:#f8fafc;border-bottom:1px solid #e5e7eb}
            .s78-modal-icon{width:44px;height:44px;display:flex;align-items:center;justify-content:center;border-radius:50%;background:#fff4cc;color:#8a6500;font-size:20px;flex:0 0 auto}
            .s78-modal-head h3{margin:0;color:#1a2e35;font-size:18px;font-weight:800}.s78-modal-head p{margin:3px 0 0;color:#6b7280;font-size:12px}
            .s78-modal-body{padding:20px 22px;color:#374151;font-size:14px;line-height:1.6}.s78-modal-list{margin-top:12px;padding:12px 14px;border-radius:8px;background:#fff7ed;border-left:4px solid #e6b000;color:#7c4a03}
            .s78-modal-foot{display:flex;justify-content:flex-end;padding:14px 22px 20px}.s78-modal-btn{border:0;border-radius:8px;background:#006570;color:#fff;padding:10px 18px;font-weight:700;cursor:pointer}.s78-modal-btn:hover{background:#004f58}
        `;
        document.head.appendChild(style);

        var modal = document.createElement("div");
        modal.id = "section78SameValueModal";
        modal.className = "s78-modal-backdrop";
        modal.innerHTML = `
            <div class="s78-modal" role="dialog" aria-modal="true" aria-labelledby="s78SameTitle">
                <div class="s78-modal-head">
                    <div class="s78-modal-icon"><i class="fa-solid fa-triangle-exclamation"></i></div>
                    <div><h3 id="s78SameTitle">Different Values Required</h3><p id="s78SameSub">Section 78 validation</p></div>
                </div>
                <div class="s78-modal-body">
                    <div id="s78SameMessage"></div>
                    <div id="s78SameList" class="s78-modal-list"></div>
                </div>
                <div class="s78-modal-foot"><button type="button" class="s78-modal-btn" id="s78SameClose">Okay, I will update the values</button></div>
            </div>`;
        document.body.appendChild(modal);

        document.getElementById("s78SameClose").addEventListener("click", closeModal);
        modal.addEventListener("click", function (e) { if (e.target === modal) closeModal(); });
    }

    function showModal(duplicates) {
        ensureModal();
        var mode = modeName();
        document.getElementById("s78SameTitle").textContent = mode + " Details Required";
        document.getElementById("s78SameSub").textContent = mode + " cannot continue with unchanged valuation details";
        document.getElementById("s78SameMessage").textContent =
            "You cannot submit a Section 78 " + mode.toLowerCase() + " using the same details that are already reflected on the Valuation Roll / MVD. Please select or enter a different value.";
        var list = document.getElementById("s78SameList");
        list.innerHTML = "<strong>Same values found:</strong><br>" + duplicates.map(function (x) { return "• " + x.label; }).join("<br>");
        document.getElementById("section78SameValueModal").style.display = "flex";
        document.body.style.overflow = "hidden";
    }

    function closeModal() {
        var modal = document.getElementById("section78SameValueModal");
        if (modal) modal.style.display = "none";
        document.body.style.overflow = "";
    }

    function samePair(pair) {
        var newer = document.getElementById(pair.newId);
        var older = document.getElementById(pair.oldId);
        if (!newer || !older || !String(newer.value || "").trim()) return false;
        var newValue = normalise(newer.value, pair.type);
        var oldValue = normalise(older.value, pair.type);
        return newValue !== "" && oldValue !== "" && newValue === oldValue;
    }

    function duplicatePairs() {
        return pairs().filter(samePair);
    }

    document.addEventListener("DOMContentLoaded", function () {
        ensureModal();

        pairs().forEach(function (pair) {
            var input = document.getElementById(pair.newId);
            if (!input) return;
            var eventName = input.tagName === "SELECT" ? "change" : "input";
            input.addEventListener(eventName, function () { clearError(input); });
        });
    });

    // Capture category changes before legacy Query handlers so the dropdown behaves
    // exactly like the Objection form: same category -> modal -> dropdown reset.
    document.addEventListener("change", function (event) {
        var target = event.target;
        if (!target || !["NewCat", "NewCat1", "NewCat2", "NewCat3"].includes(target.id)) return;

        var pair = pairs().find(function (x) { return x.newId === target.id; });
        if (!pair || !samePair(pair)) return;

        event.preventDefault();
        event.stopImmediatePropagation();
        setError(target);
        showModal([pair]);
        resetCategory(target);
    }, true);

    // Capture Section 6 Next before old alert-based validation. This gives Query and
    // Review the same modal/read-back behaviour as the Objection form.
    document.addEventListener("click", function (event) {
        var button = event.target && event.target.closest ? event.target.closest(".btn_n6") : null;
        if (!button) return;

        var duplicates = duplicatePairs();
        if (!duplicates.length) return;

        event.preventDefault();
        event.stopImmediatePropagation();

        duplicates.forEach(function (pair) {
            var input = document.getElementById(pair.newId);
            setError(input);
            if (pair.type === "category") resetCategory(input);
        });

        showModal(duplicates);
        var first = document.getElementById(duplicates[0].newId);
        if (first) setTimeout(function () { first.focus(); }, 250);
    }, true);
})();
