/*
 * Staff → Staff Attendance (Views/StaffAttendance). Display only: the server decides and saves everything.
 *
 *  - [data-sta-mark]: one click today, saved at once (no dialog, no time entry). POST /staff/attendance/mark; the server
 *    takes its own time (Africa/Cairo) as the arrival time for Present / Late and returns the row and the day's counts.
 *    Late is offered only after the start time (the person's own or the school's); the server checks the same rule.
 *  - [data-closes-at]: when the school clock passes the cutoff (09:30) the page reloads and shows the day closed; the
 *    server refuses any click after it anyway (a page left open cannot get past it).
 *  - [data-sta-search] / [data-sta-category] / [data-sta-dept] / [data-sta-filter]: search, category, department and status filters in place.
 *  - Record / Correct modal: the arrival time is hidden for Absent.
 *  - History & Reports: only the fields of the chosen period are shown and sent.
 */
(function () {
    "use strict";

    var doc = document;
    var busy = {};

    function $$(sel, root) { return Array.prototype.slice.call((root || doc).querySelectorAll(sel)); }
    function icon(name) { return '<svg class="icon icon--sm" aria-hidden="true"><use href="#i-' + name + '"></use></svg>'; }
    function toast(msg, tone) { if (window.App && App.toast) App.toast(msg, tone); }
    function board() { return doc.querySelector("[data-sta-board]"); }
    function token(b) { var t = b.querySelector("input[name=__RequestVerificationToken]"); return t ? t.value : ""; }

    var BADGE = { present: "badge--success", late: "badge--warning", absent: "badge--error", none: "badge--neutral" };

    function setCounts(counts) {
        if (!counts) return;
        $$("[data-sta-count]").forEach(function (el) {
            var v = counts[el.getAttribute("data-sta-count")];
            if (v !== undefined && el.textContent !== String(v)) {
                el.textContent = v;
                el.classList.remove("is-bumped"); void el.offsetWidth; el.classList.add("is-bumped");
            }
        });
    }

    function applyRow(li, row) {
        li.setAttribute("data-status", row.slug);
        li.className = "sa-row sta-row sa-row--" + row.slug + " is-just-saved";
        var status = li.querySelector("[data-sta-status]");
        status.className = "badge badge--dot " + (BADGE[row.slug] || "badge--neutral");
        status.textContent = row.status;
        li.querySelector("[data-sta-time]").textContent = row.time || "—";
        li.querySelector("[data-sta-note]").textContent = row.by ? "by " + row.by : "";
        var date = board().getAttribute("data-date");
        li.querySelector("[data-sta-actions]").innerHTML =
            '<a class="btn btn--ghost btn--sm" href="/staff/attendance/record?staffId=' + row.staffId + "&date=" + date + '" data-modal>' + icon("pencil") + "Correct</a>";
        applyFilter();
    }

    function mark(li, status) {
        var b = board();
        var id = li.getAttribute("data-staff");
        if (!b || busy[id]) return;
        busy[id] = true;
        li.classList.add("is-saving");
        li.setAttribute("aria-busy", "true");
        var body = new URLSearchParams();
        body.set("staffId", id);
        body.set("date", b.getAttribute("data-date"));
        body.set("status", status);
        fetch("/staff/attendance/mark", {
            method: "POST",
            headers: { "RequestVerificationToken": token(b), "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/x-www-form-urlencoded" },
            body: body,
            credentials: "same-origin",
        }).then(function (res) {
            return res.json().catch(function () { return { success: false, message: "The server did not answer as expected." }; });
        }).then(function (data) {
            if (data.closed) {
                toast(data.message || "Staff attendance is closed.", "warning");
                setTimeout(function () { window.location.reload(); }, 1200);
                return;
            }
            if (data.success) {
                applyRow(li, data.row);
                setCounts(data.counts);
                toast(data.message, status === "Absent" ? "info" : "success");
            } else {
                toast(data.message || "Not saved.", "error");
            }
        }).catch(function () {
            toast("Not saved — check the connection and try again.", "error");
        }).finally(function () {
            delete busy[id];
            li.classList.remove("is-saving");
            li.removeAttribute("aria-busy");
        });
    }

    doc.addEventListener("click", function (e) {
        var btn = e.target.closest("[data-sta-mark]");
        if (btn) {
            e.preventDefault();
            if (btn.disabled) return;
            mark(btn.closest("[data-sta-row]"), btn.getAttribute("data-sta-mark"));
            return;
        }
        var filter = e.target.closest("[data-sta-filter]");
        if (filter) {
            $$("[data-sta-filter]").forEach(function (f) { f.setAttribute("aria-pressed", f === filter ? "true" : "false"); });
            applyFilter();
        }
    });

    doc.addEventListener("input", function (e) { if (e.target.matches("[data-sta-search]")) applyFilter(); });
    doc.addEventListener("change", function (e) {
        if (e.target.matches("[data-sta-dept], [data-sta-category]")) applyFilter();
        if (e.target.closest("[data-sta-outcome]")) syncOutcome(e.target.closest("form"));
        if (e.target.matches("[data-sta-hist] [name=period]")) syncHistory();
    });

    function applyFilter() {
        var b = board();
        if (!b) return;
        var q = ((b.querySelector("[data-sta-search]") || {}).value || "").trim().toLowerCase();
        var dept = (b.querySelector("[data-sta-dept]") || {}).value || "";
        var category = (b.querySelector("[data-sta-category]") || {}).value || "";
        var pressed = b.querySelector("[data-sta-filter][aria-pressed=true]");
        var status = pressed ? pressed.getAttribute("data-sta-filter") : "all";
        var rows = $$("[data-sta-row]", b), shown = 0;
        rows.forEach(function (li) {
            var ok = (status === "all" || li.getAttribute("data-status") === status)
                && (!dept || li.getAttribute("data-dept") === dept)
                && (!category || li.getAttribute("data-category") === category)
                && (!q || (li.getAttribute("data-search") || "").indexOf(q) !== -1);
            li.hidden = !ok;
            if (ok) shown++;
        });
        var empty = b.querySelector("[data-sta-empty]");
        if (empty && rows.length) {
            empty.hidden = shown > 0;
            var p = empty.querySelector("p");
            if (p) p.textContent = "No staff members match.";
        }
    }

    // Present until the cutoff; Late only after the start time (the server checks the same rule). No start time: Late stays available.
    function syncButtons() {
        var b = board();
        if (!b || b.getAttribute("data-today") !== "true" || !window.SchoolClock || typeof SchoolClock.time !== "function") return;
        var now = SchoolClock.time();
        $$("[data-sta-row]", b).forEach(function (li) {
            var start = li.getAttribute("data-start");
            var present = li.querySelector("[data-sta-mark=Present]");
            var late = li.querySelector("[data-sta-mark=Late]");
            if (!start || !present || !late) return;
            var isLate = now.slice(0, 5) > start.slice(0, 5);
            late.disabled = !isLate;
            late.title = isLate ? "" : "Not late yet: the start time is " + start.slice(0, 5);
        });
    }

    function syncOutcome(form) {
        if (!form) return;
        var checked = form.querySelector("[data-sta-outcome] input:checked");
        var field = form.querySelector("[data-sta-time-field]");
        if (!field) return;
        var absent = checked && checked.value === "Absent";
        field.hidden = absent;
        $$("input", field).forEach(function (i) { i.disabled = absent; });
    }

    function syncHistory() {
        var form = doc.querySelector("[data-sta-hist]");
        if (!form) return;
        var period = (form.querySelector("[name=period]:checked") || {}).value || "month";
        $$("[data-sta-period]", form).forEach(function (f) {
            var on = f.getAttribute("data-sta-period") === period;
            f.hidden = !on;
            $$("input, select", f).forEach(function (i) { i.disabled = !on; });
        });
    }

    // School clock (server time, not the browser's zone).
    var clock = doc.querySelector("[data-att-clock]");
    if (clock && window.SchoolClock && typeof SchoolClock.time === "function") {
        var tick = function () {
            var p = SchoolClock.time().split(":");
            var h = +p[0];
            clock.textContent = ((h % 12) || 12) + ":" + p[1] + " " + (h < 12 ? "AM" : "PM");
        };
        tick();
        setInterval(tick, 15000);
    }

    // At the cutoff the open page reloads (the server then shows the day closed, with the remaining staff Absent).
    setInterval(function () {
        var b = board();
        var at = b && b.getAttribute("data-closes-at");
        if (!at || !window.SchoolClock || b.hasAttribute("data-closing")) return;
        if (SchoolClock.time() >= at) { b.setAttribute("data-closing", ""); setTimeout(function () { window.location.reload(); }, 2000); }
    }, 5000);

    applyFilter();
    syncButtons();
    setInterval(syncButtons, 15000);
    syncHistory();
    doc.addEventListener("app:enhance", function () {
        applyFilter();
        $$("[data-sta-form]").forEach(syncOutcome);
    });
    // Modal forms arrive after load: set the time field as soon as one appears.
    new MutationObserver(function () { $$("[data-sta-form]:not([data-sta-ready])").forEach(function (f) { f.setAttribute("data-sta-ready", ""); syncOutcome(f); }); })
        .observe(doc.body, { childList: true, subtree: true });
})();
