/*
 * Belongings (Personal and Academic): one-click daily checks and History & Reports filters.
 *
 *  - [data-bl-board]: a class page. Each [data-bl-mark] button saves one status at once (POST, JSON); the server answers
 *    with the student's row redrawn from the same partial and the class counts, so the page always shows what is stored.
 *    [data-bl-all] marks the items not checked yet as Available; [data-bl-notes-save] saves the notes.
 *    A failed save leaves the row as it was and says why (toast). Nothing is saved for the whole class at once.
 *  - [data-bl-search] / [data-bl-pending]: find a student / show only students still to check.
 *  - [data-bl-class-search]: find a class on the Daily Checks page.
 *  - [data-bl-period]: History filters show only the fields of the chosen period (the others are not sent).
 */
(function () {
    "use strict";

    var doc = document;
    function $$(sel, root) { return Array.prototype.slice.call((root || doc).querySelectorAll(sel)); }
    function toast(message, type) { if (window.App && window.App.toast) window.App.toast(message, type || "success"); }

    // ---------------------------------------------------------------- saving
    function post(board, path, fields) {
        var token = board.querySelector("input[name=__RequestVerificationToken]");
        var body = new URLSearchParams();
        body.set("date", board.getAttribute("data-date"));
        if (board.hasAttribute("data-session")) body.set("sessionId", board.getAttribute("data-session"));
        Object.keys(fields).forEach(function (k) { body.set(k, fields[k]); });
        return fetch(board.getAttribute("data-mark-url") + "/" + path, {
            method: "POST",
            credentials: "same-origin",
            headers: { "RequestVerificationToken": token ? token.value : "", "X-Requested-With": "XMLHttpRequest", "Content-Type": "application/x-www-form-urlencoded" },
            body: body.toString(),
        }).then(function (res) {
            return res.json().catch(function () { return { success: false, message: "Something went wrong (" + res.status + "). Nothing was saved." }; });
        });
    }

    function busy(row, on) {
        row.classList.toggle("is-saving", on);
        row.setAttribute("aria-busy", on ? "true" : "false");
    }

    function apply(board, row, data) {
        if (data.row) {
            var holder = doc.createElement("div");
            holder.innerHTML = data.row.trim();
            var fresh = holder.firstElementChild;
            row.replaceWith(fresh);
            row = fresh;
            if (window.App && window.App.enhance) window.App.enhance(fresh);
        }
        if (data.counts) {
            Object.keys(data.counts).forEach(function (key) {
                $$('[data-bl-count="' + key + '"]').forEach(function (el) {
                    var v = data.counts[key];
                    el.textContent = key === "percent" ? v + "%" : v;
                    if (key === "state") {
                        el.className = "bl-state " + (v === "Completed" ? "bl-state--done" : v === "In Progress" ? "bl-state--progress" : "bl-state--idle");
                    }
                });
            });
        }
        filterRows(board);
        return row;
    }

    function save(board, row, path, fields, focusSelector) {
        if (row.classList.contains("is-saving")) return;
        busy(row, true);
        post(board, path, Object.assign({ studentId: row.getAttribute("data-student") }, fields))
            .then(function (data) {
                if (!data || !data.success) {
                    busy(row, false);
                    toast((data && data.message) || "This could not be saved.", "error");
                    return;
                }
                var fresh = apply(board, row, data);
                toast(data.message, "success");
                if (focusSelector) {
                    var target = fresh.querySelector(focusSelector);
                    if (target) target.focus();
                }
            })
            .catch(function () {
                busy(row, false);
                toast("No connection to the server. Nothing was saved.", "error");
            });
    }

    doc.addEventListener("click", function (e) {
        var board = e.target.closest("[data-bl-board]");
        if (!board) return;
        var row = e.target.closest("[data-bl-row]");
        if (!row) return;

        var mark = e.target.closest("[data-bl-mark]");
        if (mark && !mark.disabled) {
            if (mark.getAttribute("aria-pressed") === "true") return; // already saved with this status
            var item = mark.getAttribute("data-item"), status = mark.getAttribute("data-status");
            save(board, row, "mark", { item: item, status: status },
                '[data-bl-mark][data-item="' + item + '"][data-status="' + status + '"]');
            return;
        }
        if (e.target.closest("[data-bl-all]") && !e.target.closest("[data-bl-all]").disabled) {
            save(board, row, "all", {}, "[data-bl-notes-toggle]");
            return;
        }
        if (e.target.closest("[data-bl-notes-toggle]")) {
            var panel = row.querySelector(".bl-row__notes");
            var open = panel.hidden;
            panel.hidden = !open;
            $$("[data-bl-notes-toggle][aria-expanded]", row).forEach(function (b) { b.setAttribute("aria-expanded", open ? "true" : "false"); });
            if (open) { var text = panel.querySelector("textarea"); if (text) text.focus(); }
            return;
        }
        var saveNotes = e.target.closest("[data-bl-notes-save]");
        if (saveNotes && !saveNotes.disabled) {
            var notes = row.querySelector("[data-bl-notes-text]");
            save(board, row, "notes", { notes: notes ? notes.value : "" }, "[data-bl-notes-toggle]");
        }
    });

    // Ctrl / Cmd + Enter in a note saves it.
    doc.addEventListener("keydown", function (e) {
        if (e.key !== "Enter" || !(e.ctrlKey || e.metaKey) || !e.target.matches("[data-bl-notes-text]")) return;
        var btn = e.target.closest("[data-bl-row]").querySelector("[data-bl-notes-save]");
        if (btn) { e.preventDefault(); btn.click(); }
    });

    // ---------------------------------------------------------------- finding students and classes
    function filterRows(board) {
        var search = board.querySelector("[data-bl-search]");
        var pending = board.querySelector("[data-bl-pending]");
        var term = search ? search.value.trim().toLowerCase() : "";
        var onlyPending = !!(pending && pending.checked);
        var shown = 0;
        $$("[data-bl-row]", board).forEach(function (row) {
            var hide = (term && row.getAttribute("data-name").indexOf(term) < 0) || (onlyPending && row.getAttribute("data-complete") === "true");
            row.hidden = !!hide;
            if (!hide) shown++;
        });
        var empty = board.querySelector("[data-bl-search-empty]");
        if (empty) empty.hidden = shown > 0 || $$("[data-bl-row]", board).length === 0;
    }
    $$("[data-bl-board]").forEach(function (board) {
        board.addEventListener("input", function (e) { if (e.target.matches("[data-bl-search]")) filterRows(board); });
        board.addEventListener("change", function (e) { if (e.target.matches("[data-bl-pending]")) filterRows(board); });
    });

    var classSearch = doc.querySelector("[data-bl-class-search]");
    if (classSearch) {
        classSearch.addEventListener("input", function () {
            var term = classSearch.value.trim().toLowerCase();
            var shown = 0;
            $$("[data-bl-stage]").forEach(function (stage) {
                var visible = 0;
                $$("[data-bl-card]", stage).forEach(function (card) {
                    var hide = term && card.getAttribute("data-name").indexOf(term) < 0;
                    card.hidden = !!hide;
                    if (!hide) visible++;
                });
                stage.hidden = visible === 0;
                shown += visible;
            });
            var empty = doc.querySelector("[data-bl-search-empty]");
            if (empty) empty.hidden = shown > 0;
        });
    }

    // ---------------------------------------------------------------- history filters
    var period = doc.querySelector("[data-bl-period]");
    if (period) {
        var form = period.form;
        var sync = function () {
            $$("[data-bl-when]", form).forEach(function (field) {
                var on = field.getAttribute("data-bl-when") === period.value;
                field.hidden = !on;
                $$("input, select", field).forEach(function (el) { el.disabled = !on; });
            });
        };
        period.addEventListener("change", sync);
        sync();
    }
})();
