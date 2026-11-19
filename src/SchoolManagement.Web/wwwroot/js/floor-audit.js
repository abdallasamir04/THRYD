/*
 * Floor Audit pages.
 *  - [data-fa-live]: the floors board / a floor board reloads itself every 30 seconds (as a partial, from the attribute's
 *    URL) while the tab is visible and no dialog is open; [data-fa-autorefresh] lists (today's Student Access) too.
 *  - [data-fa-since="<UTC ISO>"]: an open movement's time outside so far, updated in the browser between reloads. It is
 *    only displayed: nothing is ever saved from it (a return time is recorded only with "Mark Returned").
 *  - Record Student Movement: choosing the student fills in their classroom as "From"; "Somewhere else" shows the
 *    custom destination field.
 */
(function () {
    "use strict";

    var doc = document;
    var INTERVAL = 30000;

    function busy() {
        return doc.hidden || doc.querySelector("dialog[open]") || (doc.activeElement && doc.activeElement.closest && doc.activeElement.closest("[data-fa-live] form"));
    }

    function reload(el, url) {
        if (busy()) return;
        fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" }, credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.text() : null; })
            .then(function (html) {
                if (html === null || busy()) return;
                var open = Array.prototype.map.call(el.querySelectorAll("details"), function (d) { return d.open; });
                el.innerHTML = html;
                Array.prototype.forEach.call(el.querySelectorAll("details"), function (d, i) { if (open[i]) d.open = true; });
                if (window.App && App.enhance) App.enhance(el);
                tick();
            })
            .catch(function () { /* next interval */ });
    }

    function refreshAll() {
        Array.prototype.forEach.call(doc.querySelectorAll("[data-fa-live]"), function (el) { reload(el, el.getAttribute("data-fa-live")); });
        Array.prototype.forEach.call(doc.querySelectorAll("[data-fa-autorefresh]"), function (el) { reload(el, window.location.pathname + window.location.search); });
    }

    if (doc.querySelector("[data-fa-live], [data-fa-autorefresh]")) {
        setInterval(refreshAll, INTERVAL);
        doc.addEventListener("visibilitychange", function () { if (!doc.hidden) refreshAll(); });
    }

    // ---------------------------------------------------------------- time outside so far
    function duration(ms) {
        var minutes = Math.max(0, Math.floor(ms / 60000));
        if (minutes < 60) return minutes + " min";
        var m = minutes % 60;
        return Math.floor(minutes / 60) + " h " + (m < 10 ? "0" : "") + m + " min";
    }

    function tick() {
        var now = Date.now();
        Array.prototype.forEach.call(doc.querySelectorAll("[data-fa-since]"), function (el) {
            var since = Date.parse(el.getAttribute("data-fa-since"));
            if (!isNaN(since)) el.textContent = duration(now - since);
        });
    }
    setInterval(tick, 15000);

    // ---------------------------------------------------------------- Record Student Movement
    function selected(select) { return select && select.selectedOptions ? select.selectedOptions[0] : null; }

    doc.addEventListener("change", function (e) {
        var form = e.target.closest && e.target.closest("[data-fa-movement]");
        if (!form) return;
        if (e.target.matches("[data-fa-student]")) {
            // The class, the room left from and its floor come from the student's record.
            var opt = selected(e.target);
            var from = form.querySelector("[data-fa-from]");
            var cls = form.querySelector("[data-fa-class]");
            var fromFloor = form.querySelector("[data-fa-from-floor]");
            var outside = form.querySelector("[data-fa-outside-hint]");
            var room = opt && opt.value ? opt.getAttribute("data-room") : "";
            if (cls) cls.textContent = opt && opt.value ? (opt.getAttribute("data-class") || "—") : "Filled in from the student's record";
            if (outside) outside.hidden = !(opt && opt.getAttribute("data-outside"));
            if (from) {
                from.value = room || "";
                from.dispatchEvent(new Event("change", { bubbles: true }));
            }
            if (fromFloor && opt && opt.value) {
                var floor = opt.getAttribute("data-floor");
                fromFloor.textContent = room && floor ? "Source floor: " + floor + " (their class's classroom; change it if they are elsewhere)."
                    : "Class " + (opt.getAttribute("data-class") || "") + " has no classroom on a floor yet: choose the room they are leaving, or leave it empty.";
            }
        }
        if (e.target.matches("[data-fa-from]") && e.target.value) {
            var fo = selected(e.target);
            var ff = form.querySelector("[data-fa-from-floor]");
            if (ff && fo) ff.textContent = "Source floor: " + (fo.getAttribute("data-floor") || "—");
        }
        if (e.target.matches("[data-fa-to]")) {
            var to = selected(e.target);
            var tf = form.querySelector("[data-fa-to-floor]");
            if (tf) tf.textContent = to && to.value ? "Destination floor: " + (to.getAttribute("data-floor") || "—") : "Classrooms and facilities of every floor.";
        }
        if (e.target.matches("[data-fa-custom]")) {
            var field = form.querySelector("[data-fa-custom-field]");
            var to = form.querySelector("[data-fa-to]");
            if (field) field.hidden = !e.target.checked;
            if (to) {
                to.disabled = e.target.checked;
                to.dispatchEvent(new Event("change", { bubbles: true }));
            }
            if (e.target.checked && field) { var input = field.querySelector("input"); if (input) input.focus(); }
        }
    });
    // ---------------------------------------------------------------- inspection: + Add Observation / Issue
    function renumber(list) {
        Array.prototype.forEach.call(list.querySelectorAll("[data-fa-extra]"), function (row, i) {
            Array.prototype.forEach.call(row.querySelectorAll("[name^='Extras[']"), function (field) {
                field.name = field.name.replace(/^Extras\[\d+\]/, "Extras[" + i + "]");
            });
        });
    }

    doc.addEventListener("click", function (e) {
        var add = e.target.closest && e.target.closest("[data-fa-extra-add]");
        if (add) {
            var box = add.closest("[data-fa-extras]");
            var list = box.querySelector("[data-fa-extras-list]");
            var rows = list.querySelectorAll("[data-fa-extra]");
            var last = rows[rows.length - 1];
            var copy = last.cloneNode(true);
            Array.prototype.forEach.call(copy.querySelectorAll("textarea"), function (t) { t.value = ""; });
            Array.prototype.forEach.call(copy.querySelectorAll("select"), function (s) { s.selectedIndex = 0; });
            Array.prototype.forEach.call(copy.querySelectorAll("input[type=radio]"), function (r, i) { r.checked = i === 0; });
            copy.classList.remove("is-issue");
            // Renamed before it joins the form, so its radios never share a group with the row it was copied from.
            Array.prototype.forEach.call(copy.querySelectorAll("[name^='Extras[']"), function (field) {
                field.name = field.name.replace(/^Extras\[\d+\]/, "Extras[" + rows.length + "]");
            });
            list.appendChild(copy);
            var text = copy.querySelector("textarea");
            if (text) text.focus();
            return;
        }
        var remove = e.target.closest && e.target.closest("[data-fa-extra-remove]");
        if (remove) {
            var row = remove.closest("[data-fa-extra]");
            var lst = row.parentNode;
            if (lst.querySelectorAll("[data-fa-extra]").length > 1) row.remove();
            else {
                Array.prototype.forEach.call(row.querySelectorAll("textarea"), function (t) { t.value = ""; });
                Array.prototype.forEach.call(row.querySelectorAll("select"), function (s) { s.selectedIndex = 0; });
            }
            renumber(lst);
        }
    });

    doc.addEventListener("change", function (e) {
        var row = e.target.closest && e.target.closest("[data-fa-extra]");
        if (row && e.target.matches("input[type=radio]")) row.classList.toggle("is-issue", e.target.value === "issue" && e.target.checked);
    });
})();
