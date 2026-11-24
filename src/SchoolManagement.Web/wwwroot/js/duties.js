/*
 * Duties (Views/Duties): display helpers only — every status is calculated on the server from the school clock.
 *
 *  - [data-duty-live]: the board reloads itself every 30 seconds (as a partial, from the attribute's URL — on Duties &
 *    Audit the board's own address, since the page's address answers with the audit table — or else the page's URL), so
 *    PENDING → IN PROGRESS → COVERED / NOT COVERED follow the clock without a page reload. Paused while the tab is
 *    hidden or a dialog is open.
 *  - [data-days]: "Every school day" switch hides / shows the weekday choices.
 *  - [data-subduty-list]: Add Duty's Sub-Duty rows (Add Sub-Duty / remove).
 */
(function () {
    "use strict";

    var doc = document;
    var INTERVAL = 30000;

    function refresh(el) {
        if (doc.hidden || doc.querySelector("dialog[open]")) return;
        var url = el.getAttribute("data-duty-live") || (window.location.pathname + window.location.search);
        fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" }, credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.text() : null; })
            .then(function (html) {
                if (html === null) return;
                // Keep open Sub-Duty lists open across the refresh.
                var open = Array.prototype.map.call(el.querySelectorAll("details[open]"), function (d, i) { return i; });
                el.innerHTML = html;
                var details = el.querySelectorAll("details");
                open.forEach(function (i) { if (details[i]) details[i].open = true; });
                if (window.App && App.enhance) App.enhance(el);
            })
            .catch(function () { /* next tick */ });
    }

    Array.prototype.forEach.call(doc.querySelectorAll("[data-duty-live]"), function (el) {
        setInterval(function () { refresh(el); }, INTERVAL);
    });
    doc.addEventListener("visibilitychange", function () {
        if (!doc.hidden) Array.prototype.forEach.call(doc.querySelectorAll("[data-duty-live]"), refresh);
    });

    // Add Duty: Sub-Duty rows. A new row is the <template> with a fresh key (SubDuties.Index keeps keys free-form),
    // enhanced like the rest of the form (time pickers). The last row is cleared instead of removed.
    var rowKey = 0;
    doc.addEventListener("click", function (e) {
        var add = e.target.closest("[data-subduty-add]");
        if (add) {
            var list = add.closest("[data-subduty-list]");
            var tpl = list && list.querySelector("template[data-subduty-template]");
            if (!tpl) return;
            var key = "n" + (++rowKey) + "x" + Date.now().toString(36);
            var holder = doc.createElement("div");
            holder.innerHTML = tpl.innerHTML.replace(/__i__/g, key);
            var row = holder.firstElementChild;
            tpl.parentNode.insertBefore(row, tpl);
            if (window.App && App.enhance) App.enhance(row);
            var name = row.querySelector("input[type=text], input:not([type])");
            if (name) name.focus();
            return;
        }
        var remove = e.target.closest("[data-subduty-remove]");
        if (remove) {
            var current = remove.closest("[data-subduty-row]");
            var all = current.parentNode.querySelectorAll("[data-subduty-row]");
            if (all.length > 1) { current.remove(); return; }
            Array.prototype.forEach.call(current.querySelectorAll("input:not([type=hidden]):not([type=checkbox])"), function (i) { i.value = ""; });
        }
    });

    doc.addEventListener("change", function (e) {
        var all = e.target.closest("[data-days-all]");
        if (!all) return;
        var list = all.closest("[data-days]").querySelector("[data-days-list]");
        if (list) list.hidden = all.checked;
    });
})();
