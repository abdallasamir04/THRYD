/*
 * Organizational structure views (Organizational Chart, School Leadership, Operation Team, tier views, Departments
 * Leadership): search and tier filter on the loaded page, and expand / collapse of the chart's branches.
 *
 *  - form[data-org-filter="#target"]: the filter for the structure inside #target.
 *  - [data-org-search]: matches a position's title, role, unit, tier, departments and its holders' names and phone
 *    numbers (from the item's data-search; phone numbers also by their digits alone).
 *  - [data-org-tier-filter]: shows one tier ([data-org-tier-id]).
 *  - [data-org-item]: one position (hidden when it does not match); [data-org-group]: a unit or tier, hidden when none of
 *    its positions is shown; [data-org-empty]: shown when nothing matches; [data-org-count]: "n of m positions".
 *  - [data-org-expand] / [data-org-collapse]: open or close every branch (<details>) inside the target.
 */
(function () {
    "use strict";

    function norm(s) { return (s || "").toLowerCase().replace(/\s+/g, " ").trim(); }
    function digits(s) { return (s || "").replace(/\D+/g, ""); }

    function setup(form) {
        var target = document.querySelector(form.getAttribute("data-org-filter"));
        if (!target) return;
        var input = form.querySelector("[data-org-search]");
        var tierSelect = form.querySelector("[data-org-tier-filter]");
        var count = form.querySelector("[data-org-count]");
        var empty = form.nextElementSibling && form.nextElementSibling.hasAttribute("data-org-empty") ? form.nextElementSibling : null;
        var items = Array.prototype.slice.call(target.querySelectorAll("[data-org-item]"));
        // Deepest groups first, so a unit is decided before the tier around it.
        var groups = Array.prototype.slice.call(target.querySelectorAll("[data-org-group]")).reverse();

        function apply() {
            var term = norm(input && input.value);
            var termDigits = digits(input && input.value);
            var tier = tierSelect ? tierSelect.value : "";
            var shown = 0;
            items.forEach(function (item) {
                var tierBox = item.closest("[data-org-tier-id]");
                var inTier = !tier || (tierBox && tierBox.getAttribute("data-org-tier-id") === tier);
                var text = item.getAttribute("data-search") || "";
                var match = !term || text.indexOf(term) >= 0 || (termDigits.length >= 3 && digits(text).indexOf(termDigits) >= 0);
                var visible = inTier && match;
                item.hidden = !visible;
                if (visible) shown++;
            });
            groups.forEach(function (group) {
                var tierId = group.getAttribute("data-org-tier-id");
                if (tierId && tier && tierId !== tier) { group.hidden = true; return; }
                var any = group.querySelector("[data-org-item]:not([hidden])");
                // A group with no positions at all stays visible when nothing is being filtered (its empty state shows).
                var hasItems = group.querySelector("[data-org-item]");
                group.hidden = (term || tier) ? !any : false;
                if (!hasItems && !(term || tier)) group.hidden = false;
                // Searching opens the branches that contain a match.
                if (term && any) Array.prototype.forEach.call(group.querySelectorAll("details"), function (d) { if (d.querySelector("[data-org-item]:not([hidden])")) d.open = true; });
            });
            if (empty) empty.hidden = shown > 0 || items.length === 0;
            if (count) count.textContent = (term || tier) ? shown + " of " + items.length + " positions" : "";
        }

        if (input) input.addEventListener("input", apply);
        if (tierSelect) tierSelect.addEventListener("change", apply);
        form.addEventListener("click", function (e) {
            var expand = e.target.closest("[data-org-expand]");
            var collapse = e.target.closest("[data-org-collapse]");
            if (!expand && !collapse) return;
            Array.prototype.forEach.call(target.querySelectorAll("details"), function (d) { d.open = !!expand; });
        });
        if ((input && input.value) || (tierSelect && tierSelect.value)) apply();
    }

    Array.prototype.forEach.call(document.querySelectorAll("form[data-org-filter]"), setup);
})();
