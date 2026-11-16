/*
 * School Attendance → Daily Attendance (Views/Arrivals/_Daily.cshtml). Display only: the server decides everything.
 *
 *  - [data-att-arrive]: one click = the student arrived now. POST /arrivals/register; the server takes its own time
 *    (Africa/Cairo) and returns the row (Present / Late, time) and the class's counts from the database.
 *    A student already recorded is never recorded again (409 + the existing record).
 *  - [data-att-absent]: explicit absence after a light confirmation (POST /arrivals/absent).
 *  - [data-att-search] / [data-att-filter]: client-side search (name / ID / class) and status filter.
 *  - [data-att-clock]: the school clock, ticking from the server's time (not the browser's time zone).
 *  - [data-closes-at]: when the school clock reaches the cutoff (09:30) the register reloads and locks; the server refuses
 *    any arrival from the cutoff on anyway (a page left open, a refresh or a direct request cannot get past it).
 * The register is re-bound after every AJAX reload of #sa-daily (class change), via "app:enhance".
 */
(function () {
    "use strict";

    var doc = document;
    var busy = {};

    function $$(sel, root) { return Array.prototype.slice.call((root || doc).querySelectorAll(sel)); }
    function icon(name) { return '<svg class="icon" aria-hidden="true"><use href="#i-' + name + '"></use></svg>'; }
    function toast(msg, tone) { if (window.App && App.toast) App.toast(msg, tone); }
    function board() { return doc.querySelector("[data-att-board]"); }
    function token(b) { var t = b.querySelector("input[name=__RequestVerificationToken]"); return t ? t.value : ""; }

    var BADGE = { present: "badge--success", late: "badge--warning", absent: "badge--error", notrecorded: "badge--neutral" };

    function setCounts(counts) {
        if (!counts) return;
        $$("[data-att-count]").forEach(function (el) {
            var v = counts[el.getAttribute("data-att-count")];
            if (v !== undefined && el.textContent !== String(v)) {
                el.textContent = v;
                el.classList.remove("is-bumped"); void el.offsetWidth; el.classList.add("is-bumped");
            }
        });
    }

    // The row as the server now has it: status, time, and Correct in place of the actions.
    function applyRow(li, row) {
        if (!li || !row) return;
        li.setAttribute("data-status", row.slug);
        li.className = "sa-row sa-row--" + row.slug + " is-just-saved";
        var status = li.querySelector("[data-att-status]");
        status.className = "badge badge--dot " + (BADGE[row.slug] || "badge--neutral");
        status.textContent = row.status;
        li.querySelector("[data-att-time]").textContent = row.time || "—";
        var main = li.querySelector("button.sa-row__main");
        if (main) {
            var div = doc.createElement("div");
            div.className = "sa-row__main";
            div.innerHTML = main.innerHTML;
            main.replaceWith(div);
        }
        li.querySelector("[data-att-actions]").innerHTML = row.recordId
            ? '<a class="btn btn--ghost btn--sm" href="/arrivals/' + row.recordId + '/correct" data-modal>' + icon("pencil") + "Correct</a>"
            : "";
        applyFilter();
    }

    function post(url, li, extra) {
        var b = board();
        var id = li.getAttribute("data-student");
        if (!b || busy[id]) return;
        busy[id] = true;
        li.classList.add("is-saving");
        li.setAttribute("aria-busy", "true");
        var body = new URLSearchParams();
        body.set("studentId", id);
        body.set("date", b.getAttribute("data-date"));
        if (b.getAttribute("data-class")) body.set("classId", b.getAttribute("data-class"));
        fetch(url, {
            method: "POST",
            headers: { "RequestVerificationToken": token(b), "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/x-www-form-urlencoded" },
            body: body,
            credentials: "same-origin",
        }).then(function (res) {
            return res.json().catch(function () { return { success: false, message: "The server did not answer as expected." }; });
        }).then(function (data) {
            if (data.closed) {
                // Registration closed at the cutoff: show the server's message and reload the locked register.
                toast(data.message || "Arrival registration is closed.", "warning");
                setTimeout(function () { window.location.reload(); }, 1200);
                return;
            }
            if (data.row) applyRow(li, data.row);
            setCounts(data.counts);
            if (data.success) toast(data.message, extra === "absent" ? "info" : "success");
            else toast(data.message || "Not saved.", data.duplicate ? "info" : "error");
        }).catch(function () {
            toast("Not saved — check the connection and try again.", "error");
        }).finally(function () {
            delete busy[id];
            li.classList.remove("is-saving");
            li.removeAttribute("aria-busy");
        });
    }

    doc.addEventListener("click", function (e) {
        var arrive = e.target.closest("[data-att-arrive]");
        if (arrive) {
            e.preventDefault();
            post("/arrivals/register", arrive.closest("[data-att-row]"), "arrive");
            return;
        }
        var absent = e.target.closest("[data-att-absent]");
        if (absent) {
            e.preventDefault();
            var li = absent.closest("[data-att-row]");
            var name = absent.getAttribute("data-name") || "this student";
            var go = function () { post("/arrivals/absent", li, "absent"); };
            if (window.App && App.confirm) {
                App.confirm({
                    title: "Mark " + name + " absent?",
                    message: "Only for a student confirmed absent today. It can be corrected later (with a reason).",
                    confirmText: "Mark absent",
                    cancelText: "Cancel",
                    tone: "warning", confirmTone: "danger", focus: "cancel",
                }).then(function (ok) { if (ok) go(); });
            } else if (window.confirm("Mark " + name + " absent?")) go();
            return;
        }
        var filter = e.target.closest("[data-att-filter]");
        if (filter) {
            $$("[data-att-filter]").forEach(function (b) { b.setAttribute("aria-pressed", b === filter ? "true" : "false"); });
            applyFilter();
        }
    });

    doc.addEventListener("input", function (e) {
        if (e.target.matches("[data-att-search]")) applyFilter();
    });

    function applyFilter() {
        var b = board();
        if (!b) return;
        var q = ((b.querySelector("[data-att-search]") || {}).value || "").trim().toLowerCase();
        var pressed = b.querySelector("[data-att-filter][aria-pressed=true]");
        var status = pressed ? pressed.getAttribute("data-att-filter") : "all";
        var shown = 0, rows = $$("[data-att-row]", b);
        rows.forEach(function (li) {
            var ok = (status === "all" || li.getAttribute("data-status") === status)
                && (!q || (li.getAttribute("data-search") || "").indexOf(q) !== -1);
            li.hidden = !ok;
            if (ok) shown++;
        });
        var empty = b.querySelector("[data-att-empty]");
        if (empty && rows.length) {
            empty.hidden = shown > 0;
            var p = empty.querySelector("p");
            if (p) p.textContent = "No students match.";
        }
    }

    // ---------------------------------------------------------------- school clock (server time, not the browser's zone)
    var clock = doc.querySelector("[data-att-clock]");
    if (clock && window.SchoolClock && typeof SchoolClock.time === "function") {
        var tick = function () {
            var p = SchoolClock.time().split(":"); // "HH:mm:ss", school time
            var h = +p[0];
            clock.textContent = ((h % 12) || 12) + ":" + p[1] + " " + (h < 12 ? "AM" : "PM");
        };
        tick();
        setInterval(tick, 15000);
    }


    // ---------------------------------------------------------------- Attendance History & Reports: the form shows only
    // the fields of the chosen report (student / class / grade / school) and period; hidden fields are not sent.
    var hist = doc.querySelector("[data-hist-form]");
    if (hist) {
        var syncHist = function () {
            var mode = (hist.querySelector("[name=mode]:checked") || {}).value || "school";
            var period = (hist.querySelector("[name=period]:checked") || {}).value || "day";
            $$("[data-hist-for]", hist).forEach(function (f) {
                var on = f.getAttribute("data-hist-for").split(" ").indexOf(mode) !== -1;
                f.hidden = !on;
                $$("input, select", f).forEach(function (i) { i.disabled = !on; });
            });
            $$("[data-hist-period]", hist).forEach(function (f) {
                var on = f.getAttribute("data-hist-period") === period;
                f.hidden = !on;
                $$("input, select", f).forEach(function (i) { i.disabled = !on; });
            });
        };
        hist.addEventListener("change", function (e) { if (e.target.matches("[name=mode], [name=period]")) syncHist(); });
        syncHist();
    }

    // At the cutoff the open register reloads (the server then shows it closed, with the students locked as Absent).
    setInterval(function () {
        var b = board();
        var at = b && b.getAttribute("data-closes-at");
        if (!at || !window.SchoolClock || b.hasAttribute("data-closing")) return;
        if (SchoolClock.time() >= at) { b.setAttribute("data-closing", ""); window.location.reload(); }
    }, 5000);

    doc.addEventListener("app:enhance", function () { applyFilter(); });
})();
