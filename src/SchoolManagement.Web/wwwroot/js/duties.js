/*
 * Duties (Views/Duties): display helpers only — every status is calculated on the server from the school clock.
 *
 *  - [data-duty-live]: the board reloads itself every 30 seconds (same URL, as a partial), so PENDING → IN PROGRESS →
 *    COVERED / NOT COVERED follow the clock without a page reload. Paused while the tab is hidden or a dialog is open.
 *  - [data-days]: "Every school day" switch hides / shows the weekday choices.
 */
(function () {
    "use strict";

    var doc = document;
    var INTERVAL = 30000;

    function refresh(el) {
        if (doc.hidden || doc.querySelector("dialog[open]")) return;
        fetch(window.location.pathname + window.location.search, { headers: { "X-Requested-With": "XMLHttpRequest" }, credentials: "same-origin" })
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

    doc.addEventListener("change", function (e) {
        var all = e.target.closest("[data-days-all]");
        if (!all) return;
        var list = all.closest("[data-days]").querySelector("[data-days-list]");
        if (list) list.hidden = all.checked;
    });
})();
