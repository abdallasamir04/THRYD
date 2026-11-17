/*
 * Session Attendance (Views/Attendance/SessionClass.cshtml, _SessionRegister.cshtml, SessionReports.cshtml).
 * Display only: the server decides and validates everything (lesson started, school-absent students locked, alerts).
 *
 *  - [data-mark]: one click = Present / Late / Absent for one student, saved at once (POST /attendance/sessions/{id}/mark).
 *    The row, the counts and "Alert raised" update from the response; an alert is announced with its text.
 *  - [data-ses-search]: find a student in the register.
 *  - [data-ses-clock]: the school clock (Cairo) on the lesson panel.
 *  - [data-ses-report-form]: shows only the fields of the chosen report; hidden fields are not sent.
 */
(function () {
    "use strict";

    var doc = document;
    var busy = {};
    function $$(sel, root) { return Array.prototype.slice.call((root || doc).querySelectorAll(sel)); }
    function toast(msg, tone) { if (window.App && App.toast) App.toast(msg, tone); }

    var SLUG = { Present: "present", Late: "late", Absent: "absent", Excused: "excused" };

    function counts(c) {
        if (!c) return;
        $$("[data-ses-count]").forEach(function (el) {
            var v = c[el.getAttribute("data-ses-count")];
            if (v !== undefined && el.textContent !== String(v)) {
                el.textContent = v;
                el.classList.remove("is-bumped"); void el.offsetWidth; el.classList.add("is-bumped");
            }
        });
    }

    function applyRow(li, row) {
        if (!li || !row) return;
        li.className = li.className.replace(/\bses-row--\w+/g, "").trim() + " ses-row--" + (SLUG[row.status] || "none") + (row.alerted ? " has-alert" : "");
        if (!row.alerted) li.classList.remove("has-alert");
        $$("[data-mark]", li).forEach(function (b) { b.setAttribute("aria-pressed", b.getAttribute("data-mark") === row.status ? "true" : "false"); });
        var time = li.querySelector("[data-ses-time]");
        if (time) time.textContent = row.time || "—";
        var by = li.querySelector("[data-ses-by]");
        if (by) by.textContent = (row.by || "") + (row.corrections > 0 ? " · corrected " + row.corrections + "×" : "");
        var alert = li.querySelector("[data-ses-alert]");
        if (alert) alert.hidden = !row.alerted;
        li.classList.remove("is-just-saved"); void li.offsetWidth; li.classList.add("is-just-saved");
    }

    doc.addEventListener("click", function (e) {
        var btn = e.target.closest("[data-mark]");
        if (!btn || btn.disabled) return;
        var reg = btn.closest("[data-ses-register]");
        var li = btn.closest("[data-ses-row]");
        if (!reg || !li) return;
        var id = li.getAttribute("data-student");
        if (busy[id] || btn.getAttribute("aria-pressed") === "true") return;
        busy[id] = true;
        li.classList.add("is-saving");
        var token = reg.querySelector("input[name=__RequestVerificationToken]");
        var body = new URLSearchParams({ studentId: id, status: btn.getAttribute("data-mark"), date: reg.getAttribute("data-date") });
        fetch("/attendance/sessions/" + reg.getAttribute("data-session") + "/mark", {
            method: "POST",
            credentials: "same-origin",
            headers: { "RequestVerificationToken": token ? token.value : "", "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/x-www-form-urlencoded" },
            body: body,
        }).then(function (res) {
            return res.json().catch(function () { return { success: false, message: "The server did not answer as expected." }; });
        }).then(function (data) {
            if (!data.success) { toast(data.message || "Not saved.", "error"); return; }
            applyRow(li, data.row);
            counts(data.counts);
            if (data.alert) toast("Alert raised — " + data.alert, "warning");
            else toast(data.message, "success");
        }).catch(function () {
            toast("Not saved — check the connection and try again.", "error");
        }).finally(function () {
            delete busy[id];
            li.classList.remove("is-saving");
        });
    });

    doc.addEventListener("input", function (e) {
        if (!e.target.matches("[data-ses-search]")) return;
        var q = e.target.value.trim().toLowerCase();
        $$("[data-ses-row]").forEach(function (li) { li.hidden = q !== "" && (li.getAttribute("data-name") || "").indexOf(q) < 0; });
    });

    // School clock (Cairo), from the server's time.
    var clocks = $$("[data-ses-clock]");
    if (clocks.length && window.SchoolClock && typeof SchoolClock.time === "function") {
        var tick = function () {
            var p = SchoolClock.time().split(":");
            var h = +p[0];
            var text = ((h % 12) || 12) + ":" + p[1] + " " + (h < 12 ? "AM" : "PM");
            clocks.forEach(function (c) { c.textContent = text; });
        };
        tick();
        setInterval(tick, 15000);
    }

    // Reports: only the fields of the chosen report.
    var form = doc.querySelector("[data-ses-report-form]");
    if (form) {
        var sync = function () {
            var mode = (form.querySelector("[name=mode]:checked") || {}).value || "school";
            $$("[data-for]", form).forEach(function (f) {
                var on = f.getAttribute("data-for").split(" ").indexOf(mode) !== -1;
                f.hidden = !on;
                $$("input, select", f).forEach(function (i) { i.disabled = !on; });
            });
        };
        form.addEventListener("change", function (e) { if (e.target.matches("[name=mode]")) sync(); });
        sync();
    }
})();
