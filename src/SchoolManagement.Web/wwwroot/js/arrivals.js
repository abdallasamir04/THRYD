/*
 * Student Arrivals checklist: fast inline entry for many students, saved together.
 * Display helpers only — the server decides late status (from the Settings deadline), rejects
 * duplicates and validates every row. Saved rows change only through "Correct" (with a reason).
 */
(function () {
    "use strict";

    var STATUS = { Present: 1, Absent: 2 };
    var METHOD = { SchoolBus: 1, PrivateCar: 2, ParentDropOff: 3 };
    var $$ = function (sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); };

    function nowTime() {
        var d = new Date();
        var p = function (n) { return (n < 10 ? "0" : "") + n; };
        return p(d.getHours()) + ":" + p(d.getMinutes()) + ":" + p(d.getSeconds());
    }

    function seconds(t) {
        if (!t) return null;
        var parts = t.split(":").map(Number);
        return parts[0] * 3600 + parts[1] * 60 + (parts[2] || 0);
    }

    function toast(message, type) {
        if (window.App && window.App.toast) window.App.toast(message, type);
    }

    function init(root) {
        var table = (root || document).querySelector("[data-arrival-table]");
        if (!table || table._ready) return;
        table._ready = true;
        var section = table.closest(".table-card");
        var deadline = seconds(table.getAttribute("data-deadline"));
        var filter = table.getAttribute("data-initial-filter") || "all";
        var term = "";
        var session = document.querySelector("[data-arrival-session]");
        var pendingLabel = section.querySelector("[data-arrival-pending]");
        var saveButton = section.querySelector("[data-arrival-save]");

        function field(row, name) { return row.querySelector('[data-field="' + name + '"]' + (name === "status" ? ":checked" : "")); }

        function paintRow(row) {
            if (row.getAttribute("data-state") !== "missing") return;
            var status = field(row, "status");
            var time = field(row, "time");
            var method = field(row, "method");
            var badge = row.querySelector("[data-row-status]");
            var absent = status && status.value === "Absent";
            time.disabled = absent;
            method.disabled = absent;
            row.querySelector("[data-now]").disabled = absent;
            if (absent) { time.value = ""; method.value = ""; }
            var late = !absent && deadline !== null && time.value && seconds(time.value) > deadline;
            row.classList.toggle("is-late-preview", !!late);
            row.classList.toggle("is-dirty", !!status);
            if (!status) { badge.className = "badge badge--neutral"; badge.textContent = "Not recorded"; }
            else if (absent) { badge.className = "badge badge--error"; badge.textContent = "Absent · unsaved"; }
            else if (!time.value) { badge.className = "badge badge--warning"; badge.textContent = "Needs a time"; }
            else { badge.className = "badge " + (late ? "badge--error" : "badge--primary"); badge.textContent = (late ? "Late" : deadline === null ? "Present" : "On time") + " · unsaved"; }
        }

        function dirtyRows() {
            return $$('.arrival-row[data-state="missing"]', table).filter(function (r) { return !!field(r, "status"); });
        }

        function updatePending() {
            var n = dirtyRows().length;
            if (pendingLabel) pendingLabel.textContent = n === 0 ? "No changes yet." : n + " entr" + (n === 1 ? "y" : "ies") + " ready to save.";
            if (saveButton) saveButton.disabled = n === 0;
        }

        function applyFilter() {
            $$("[data-arrival-filter]", section).forEach(function (b) { b.setAttribute("aria-pressed", b.getAttribute("data-arrival-filter") === filter ? "true" : "false"); });
            $$(".arrival-row", table).forEach(function (row) {
                var state = row.getAttribute("data-state");
                var show = filter === "all"
                    || (filter === "missing" && state === "missing")
                    || (filter === "late" && (state === "late" || row.classList.contains("is-late-preview")))
                    || (filter === "absent" && state === "absent");
                if (term && row.getAttribute("data-name").indexOf(term) < 0) show = false;
                row.hidden = !show;
            });
        }

        function markPresent(row) {
            var present = row.querySelector('[data-field="status"][value="Present"]');
            if (present && !field(row, "status")) present.checked = true;
            var method = row.querySelector('[data-field="method"]');
            var defaultMethod = session && session.querySelector("#session-method").value;
            if (method && !method.value && defaultMethod) method.value = defaultMethod;
        }

        table.addEventListener("click", function (e) {
            var now = e.target.closest("[data-now]");
            if (!now) return;
            var row = now.closest(".arrival-row");
            row.querySelector('[data-field="time"]').value = nowTime();
            markPresent(row);
            paintRow(row);
            updatePending();
        });
        table.addEventListener("input", function (e) {
            var row = e.target.closest(".arrival-row");
            if (!row) return;
            if (e.target.matches('[data-field="time"]') && e.target.value) markPresent(row);
            paintRow(row);
            updatePending();
        });
        table.addEventListener("change", function (e) {
            var row = e.target.closest(".arrival-row");
            if (!row) return;
            if (e.target.matches('[data-field="status"]') && e.target.value === "Present") markPresent(row);
            paintRow(row);
            updatePending();
        });

        $$("[data-arrival-filter]", section).forEach(function (b) {
            b.addEventListener("click", function () {
                filter = b.getAttribute("data-arrival-filter");
                var url = new URL(window.location.href);
                if (filter === "all") url.searchParams.delete("show"); else url.searchParams.set("show", filter);
                history.replaceState(null, "", url.pathname + url.search);
                applyFilter();
            });
        });
        var search = section.querySelector("[data-arrival-search]");
        if (search) search.addEventListener("input", function () { term = search.value.trim().toLowerCase(); applyFilter(); });

        var allPresent = section.querySelector("[data-arrival-all-present]");
        if (allPresent) allPresent.addEventListener("click", function () {
            var t = nowTime();
            $$('.arrival-row[data-state="missing"]', table).filter(function (r) { return !r.hidden && !field(r, "status"); }).forEach(function (row) {
                markPresent(row);
                row.querySelector('[data-field="time"]').value = t;
                paintRow(row);
            });
            updatePending();
        });

        if (saveButton) saveButton.addEventListener("click", function () {
            var rows = dirtyRows();
            if (rows.length === 0) return;
            var receiver = session ? session.querySelector("#session-receiver").value : "";
            var location = session ? session.querySelector("#session-location").value : "";
            var confirmed = session ? session.querySelector("#session-confirmed").checked : false;
            var entries = rows.map(function (row) {
                var status = field(row, "status").value;
                var method = row.querySelector('[data-field="method"]').value;
                return {
                    studentId: parseInt(row.getAttribute("data-student"), 10),
                    status: STATUS[status],
                    time: status === "Present" ? row.querySelector('[data-field="time"]').value || null : null,
                    method: status === "Present" && method ? METHOD[method] : null,
                    location: status === "Present" ? location || null : null,
                    receivedByStaffId: status === "Present" && receiver ? parseInt(receiver, 10) : null,
                    receivingConfirmed: status === "Present" && !!receiver && confirmed,
                    notes: row.querySelector('[data-field="notes"]').value || null,
                };
            });
            var tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
            saveButton.disabled = true;
            saveButton.classList.add("is-busy");
            fetch("/arrivals/save", {
                method: "POST",
                credentials: "same-origin",
                headers: {
                    "Content-Type": "application/json",
                    "X-Requested-With": "XMLHttpRequest",
                    "RequestVerificationToken": tokenInput ? tokenInput.value : "",
                },
                body: JSON.stringify({ date: table.getAttribute("data-date"), entries: entries }),
            })
                .then(function (r) { return r.json().catch(function () { return { success: false, message: "The server returned an unexpected answer (" + r.status + ")." }; }); })
                .then(function (data) {
                    if (!data.results) { toast(data.message || "Nothing was saved.", "error"); return; }
                    var failed = 0;
                    data.results.forEach(function (res) {
                        var row = table.querySelector('.arrival-row[data-student="' + res.studentId + '"]');
                        if (!row) return;
                        var error = row.querySelector("[data-row-error]");
                        if (res.saved) {
                            row.setAttribute("data-state", "saved");
                            $$("input, select, button", row).forEach(function (el) { el.disabled = true; });
                            var badge = row.querySelector("[data-row-status]");
                            badge.className = "badge badge--success";
                            badge.textContent = "Saved";
                            error.textContent = "";
                            row.classList.remove("has-error", "is-dirty");
                        } else {
                            failed++;
                            row.classList.add("has-error");
                            error.textContent = res.error;
                        }
                    });
                    toast(data.message, failed === 0 ? "success" : "warning");
                    if (failed === 0) reload();
                })
                .catch(function () { toast("No connection to the server. Nothing was saved.", "error"); })
                .finally(function () { saveButton.classList.remove("is-busy"); updatePending(); });
        });

        function reload() {
            var container = document.getElementById("arrival-list");
            var sk = window.App && App.skeleton;
            var previous = sk ? sk.show(container) : null; // checklist rows shimmer while the saved list reloads
            fetch(window.location.href, { headers: { "X-Requested-With": "XMLHttpRequest" }, credentials: "same-origin" })
                .then(function (r) { if (!r.ok) throw new Error(String(r.status)); return r.text(); })
                .then(function (html) {
                    container.innerHTML = html;
                    document.dispatchEvent(new CustomEvent("app:enhance", { detail: container }));
                })
                .catch(function () {
                    if (previous !== null) { container.innerHTML = previous; document.dispatchEvent(new CustomEvent("app:enhance", { detail: container })); }
                    toast("Saved, but the list could not be refreshed. Reload the page to see the latest records.", "warning");
                })
                .finally(function () { if (sk) sk.done(container); });
        }

        window.addEventListener("beforeunload", function (e) {
            if (dirtyRows().length > 0) { e.preventDefault(); e.returnValue = ""; }
        });

        $$(".arrival-row", table).forEach(paintRow);
        applyFilter();
        updatePending();
    }

    document.addEventListener("app:enhance", function (e) { init(e.detail || document); });
    init(document);
})();
