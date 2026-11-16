/*
 * Add / Edit Incident Report page (Views/Behaviour/ReportForm.cshtml). Display only: the server checks everything.
 *
 *  - Repeating entries (students, timeline events, actions): add from a <template>, remove (asking first when the
 *    card has content), move events up / down, collapse to a one-line summary. Field names are re-numbered
 *    (Students[0].StudentId…) before every submit so the server receives a clean list.
 *  - A student chosen from the Student Directory fills Grade & Section from their class (never typed).
 *  - "Students present" of each event lists the students of section 2 and keeps what was ticked.
 *  - [data-suggest]: a text field with a THRYD list of suggestions (free text still allowed).
 *  - Prepared By: department / person text, or a staff member.
 */
(function () {
    "use strict";

    var form = document.querySelector("form[data-incident-report]");
    if (!form) return;
    var doc = document;
    var uid = 0;

    function $$(sel, root) { return Array.prototype.slice.call((root || doc).querySelectorAll(sel)); }
    function esc(s) { var d = doc.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }
    function list(prefix) { return form.querySelector('[data-entries="' + prefix + '"]'); }
    function entries(prefix) { return $$(":scope > [data-entry]", list(prefix)); }
    function dirty() { form.dispatchEvent(new Event("input", { bubbles: true })); }

    // ---------------------------------------------------------------- numbering
    function renumber(prefix) {
        entries(prefix).forEach(function (entry, i) {
            var num = entry.querySelector("[data-entry-num]");
            if (num) num.textContent = i + 1;
            $$("[name]", entry).forEach(function (el) {
                el.name = el.name.replace(new RegExp("^" + prefix + "\\[[^\\]]*\\]"), prefix + "[" + i + "]");
            });
            var present = entry.querySelector("[data-present]");
            if (present) present.setAttribute("data-name", "Timeline[" + i + "].PresentStudentIds");
            $$("[data-entry-move]", entry).forEach(function (b) {
                var up = b.getAttribute("data-entry-move") === "-1";
                b.disabled = up ? i === 0 : i === entries(prefix).length - 1;
            });
        });
        var empty = form.querySelector('[data-entries-empty="' + prefix + '"]');
        if (empty) empty.hidden = entries(prefix).length > 0;
        var key = { Students: "students", Timeline: "events", Actions: "actions" }[prefix];
        $$('[data-count="' + key + '"]').forEach(function (c) { c.textContent = entries(prefix).length; });
    }
    function renumberAll() { ["Students", "Timeline", "Actions"].forEach(renumber); }

    // ---------------------------------------------------------------- open / collapse
    function setOpen(entry, open) {
        entry.classList.toggle("is-open", open);
        var t = entry.querySelector("[data-entry-toggle]");
        if (t) {
            t.setAttribute("aria-expanded", open ? "true" : "false");
            t.querySelector("span").textContent = open ? "Done" : "Edit";
        }
    }

    // ---------------------------------------------------------------- summaries shown on a collapsed card
    function summarize(entry) {
        var kind = entry.getAttribute("data-entry-kind");
        var title = entry.querySelector("[data-summary-title]");
        var meta = entry.querySelector("[data-summary-meta]");
        if (kind === "student") {
            var sel = entry.querySelector("[data-student-select]");
            var o = sel && sel.options[sel.selectedIndex];
            var chosen = o && o.value;
            title.textContent = chosen ? o.text : "New student";
            meta.textContent = chosen ? o.getAttribute("data-grade") : "Choose a student";
            var role = entry.querySelector("[data-role-select]");
            var badge = entry.querySelector("[data-summary-role]");
            if (role && badge) {
                var other = entry.querySelector('[data-field="RoleOther"]');
                var text = role.options[role.selectedIndex].text;
                badge.textContent = role.value === "Other" && other && other.value.trim() ? "Other: " + other.value.trim() : text;
                badge.className = "inc-rolebadge inc-rolebadge--" + (role.selectedIndex + 1);
            }
        } else if (kind === "event") {
            var period = entry.querySelector('[data-field="TimePeriod"]').value.trim();
            var time = entry.querySelector('[data-field="Time"]').value;
            var label = time ? to12(time) : "";
            title.textContent = [period, label].filter(Boolean).join(" · ") || "New event";
            var loc = entry.querySelector('[data-field="Location"]').value.trim();
            meta.textContent = loc || "Location not set";
        } else if (kind === "action") {
            title.textContent = entry.querySelector('[data-field="ActionItem"]').value.trim() || "New action";
            var finding = entry.querySelector('[data-field="Finding"]').value.trim();
            meta.textContent = finding || "Finding not written yet";
            var st = entry.querySelector("[data-status-select]");
            var b = entry.querySelector("[data-summary-status]");
            if (st && b) {
                var opt = st.options[st.selectedIndex];
                b.textContent = opt.text;
                b.className = "badge " + opt.getAttribute("data-badge");
            }
        }
    }
    function to12(hhmm) {
        var p = /^(\d{1,2}):(\d{2})/.exec(hhmm || "");
        if (!p) return "";
        var h = +p[1];
        return (h % 12 === 0 ? 12 : h % 12) + ":" + p[2] + " " + (h >= 12 ? "PM" : "AM");
    }

    // ---------------------------------------------------------------- student → grade & section
    function showGrade(entry) {
        var sel = entry.querySelector("[data-student-select]");
        var box = entry.querySelector("[data-grade-display]");
        if (!sel || !box) return;
        var o = sel.options[sel.selectedIndex];
        var grade = o && o.value ? o.getAttribute("data-grade") : null;
        var cls = o && o.value ? o.getAttribute("data-class") : null;
        box.innerHTML = box.querySelector("svg").outerHTML + "<span>" + esc(grade || "Filled in from the student's class") + "</span>" +
            (cls ? '<span class="inc-readonly__tag">' + esc(cls) + "</span>" : "");
        box.classList.toggle("is-filled", !!grade);
    }

    // ---------------------------------------------------------------- students present (from section 2)
    function involved() {
        return entries("Students").map(function (e) {
            var sel = e.querySelector("[data-student-select]");
            var o = sel && sel.options[sel.selectedIndex];
            return o && o.value ? { id: o.value, name: o.text, cls: o.getAttribute("data-class") || "" } : null;
        }).filter(Boolean);
    }
    function syncPresent() {
        var people = involved();
        entries("Timeline").forEach(function (entry) {
            var box = entry.querySelector("[data-present]");
            if (!box) return;
            var checked = {};
            var had = $$("input[type=checkbox]", box);
            if (had.length) had.forEach(function (c) { if (c.checked) checked[c.value] = true; });
            else (JSON.parse(box.getAttribute("data-selected") || "[]")).forEach(function (id) { checked[id] = true; });
            var name = box.getAttribute("data-name");
            box.innerHTML = people.map(function (p) {
                return '<label class="inc-chip"><input type="checkbox" name="' + esc(name) + '" value="' + esc(p.id) + '"' + (checked[p.id] ? " checked" : "") +
                    "><span>" + esc(p.name) + "<small>" + esc(p.cls) + "</small></span></label>";
            }).join("");
            var empty = entry.querySelector("[data-present-empty]");
            if (empty) empty.hidden = people.length > 0;
        });
    }

    // ---------------------------------------------------------------- add / remove / move
    function add(prefix) {
        var tpl = form.querySelector('template[data-entry-template="' + prefix + '"]');
        var html = tpl.innerHTML.replace(/__i__/g, "n" + (++uid));
        var holder = doc.createElement("div");
        holder.innerHTML = html.trim();
        var entry = holder.firstElementChild;
        list(prefix).appendChild(entry);
        renumber(prefix);
        if (window.App && App.enhance) App.enhance(entry);
        wrapSuggest(entry);
        wire(entry);
        if (prefix === "Timeline") syncPresent();
        setOpen(entry, true);
        entry.scrollIntoView({ block: "center", behavior: "smooth" });
        var first = entry.querySelector(".combo__button, input:not([type=hidden]), select, textarea");
        if (first) setTimeout(function () { first.focus(); }, 250);
        dirty();
    }
    function hasContent(entry) {
        return $$("input:not([type=hidden]):not([type=checkbox]):not([type=radio]), textarea, select[data-student-select]", entry)
            .some(function (el) { return el.value && el.value.trim() !== ""; });
    }
    function remove(entry) {
        var prefix = entry.parentNode.getAttribute("data-entries");
        var go = function () {
            entry.remove();
            renumber(prefix);
            if (prefix === "Students") syncPresent();
            dirty();
        };
        if (!hasContent(entry) || !window.App || !App.confirm) { go(); return; }
        var what = { Students: "this student", Timeline: "this timeline entry", Actions: "this action" }[prefix];
        App.confirm({ title: "Remove " + what + "?", message: "It is removed from the report when you save. Nothing changes until then.", confirmText: "Remove", tone: "danger" })
            .then(function (ok) { if (ok) go(); });
    }
    function move(entry, dir) {
        var parent = entry.parentNode;
        var sib = dir < 0 ? entry.previousElementSibling : entry.nextElementSibling;
        if (!sib) return;
        if (dir < 0) parent.insertBefore(entry, sib); else parent.insertBefore(sib, entry);
        renumber(parent.getAttribute("data-entries"));
        dirty();
    }

    // ---------------------------------------------------------------- per-entry wiring
    function wire(entry) {
        if (entry._wired) return;
        entry._wired = true;
        summarize(entry);
        entry.addEventListener("change", function (e) {
            if (e.target.matches("[data-student-select]")) { showGrade(entry); syncPresent(); }
            if (e.target.matches("[data-role-select]")) {
                var other = entry.querySelector("[data-role-other]");
                if (other) other.hidden = e.target.value !== "Other";
            }
            summarize(entry);
        });
        entry.addEventListener("input", function () { summarize(entry); });
    }

    form.addEventListener("click", function (e) {
        var addBtn = e.target.closest("[data-entry-add]");
        if (addBtn) { e.preventDefault(); add(addBtn.getAttribute("data-entry-add")); return; }
        var entry = e.target.closest("[data-entry]");
        if (!entry) return;
        if (e.target.closest("[data-entry-remove]")) { e.preventDefault(); remove(entry); }
        else if (e.target.closest("[data-entry-toggle]")) { e.preventDefault(); setOpen(entry, !entry.classList.contains("is-open")); }
        else if (e.target.closest("[data-entry-move]")) { e.preventDefault(); move(entry, +e.target.closest("[data-entry-move]").getAttribute("data-entry-move")); }
        else if (e.target.closest(".inc-entry__summary") && !entry.classList.contains("is-open")) setOpen(entry, true);
    });

    // Before saving: clean numbering, and open every card that holds a field the browser will flag.
    $$("[data-report-submit]").forEach(function (b) {
        b.addEventListener("click", function () {
            var chosen = form.querySelector("[data-intent]");
            if (chosen) chosen.value = b.value;
            renumberAll();
            if (b.formNoValidate) return;
            $$("[data-entry]", form).forEach(function (entry) {
                var bad = $$("input, select, textarea", entry).some(function (el) { return el.willValidate && !el.checkValidity(); });
                if (bad) setOpen(entry, true);
            });
        });
    });
    form.addEventListener("submit", renumberAll);

    // ---------------------------------------------------------------- Prepared By: text or staff member
    function preparedMode() {
        var mode = (form.querySelector("[data-prepared-mode]:checked") || {}).value || "text";
        $$("[data-prepared]", form).forEach(function (box) { box.hidden = box.getAttribute("data-prepared") !== mode; });
    }
    form.addEventListener("change", function (e) { if (e.target.matches("[data-prepared-mode]")) preparedMode(); });

    // ---------------------------------------------------------------- suggestions (text field + list)
    var openSuggest = null;
    function closeSuggest() { if (openSuggest) { openSuggest.remove(); openSuggest = null; } }
    function showSuggest(input) {
        var all = [];
        try { all = JSON.parse(input.getAttribute("data-suggest") || "[]"); } catch (err) { all = []; }
        var term = input.value.trim().toLowerCase();
        var items = all.filter(function (x) { return !term || x.toLowerCase().indexOf(term) >= 0; }).slice(0, 12);
        closeSuggest();
        if (!items.length || (items.length === 1 && items[0].toLowerCase() === term)) return;
        var wrap = input.closest(".inc-suggest");
        if (!wrap) return;
        var panel = doc.createElement("ul");
        panel.className = "combo__panel inc-suggest__panel";
        panel.setAttribute("role", "listbox");
        panel.innerHTML = items.map(function (x, i) {
            return '<li class="combo__option' + (i === 0 ? " is-active" : "") + '" role="option" data-value="' + esc(x) + '">' + esc(x) + "</li>";
        }).join("");
        wrap.appendChild(panel);
        var rect = wrap.getBoundingClientRect();
        if (window.innerHeight - rect.bottom < 280 && rect.top > 280) panel.classList.add("is-up");
        openSuggest = panel;
        panel.addEventListener("mousedown", function (ev) {
            var li = ev.target.closest("[data-value]");
            if (!li) return;
            ev.preventDefault();
            pick(input, li.getAttribute("data-value"));
        });
    }
    function pick(input, value) {
        input.value = value;
        closeSuggest();
        input.dispatchEvent(new Event("input", { bubbles: true }));
        input.dispatchEvent(new Event("change", { bubbles: true }));
    }
    form.addEventListener("focusin", function (e) { if (e.target.matches("[data-suggest]")) showSuggest(e.target); });
    form.addEventListener("input", function (e) { if (e.target.matches("[data-suggest]")) showSuggest(e.target); });
    form.addEventListener("focusout", function (e) { if (e.target.matches("[data-suggest]")) setTimeout(closeSuggest, 120); });
    // A long form: Enter in a one-line field never saves the report by accident (Save buttons and textareas still work).
    form.addEventListener("keydown", function (e) {
        if (e.key !== "Enter" || openSuggest) return;
        var t = e.target;
        if (t.matches("input:not([type=submit]):not([type=button]):not([type=checkbox]):not([type=radio])")) e.preventDefault();
    });
    form.addEventListener("keydown", function (e) {
        if (!openSuggest || !e.target.matches("[data-suggest]")) return;
        var opts = $$("[data-value]", openSuggest);
        var i = opts.findIndex(function (o) { return o.classList.contains("is-active"); });
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            e.preventDefault();
            if (i >= 0) opts[i].classList.remove("is-active");
            i = e.key === "ArrowDown" ? Math.min(opts.length - 1, i + 1) : Math.max(0, i - 1);
            opts[i].classList.add("is-active");
            opts[i].scrollIntoView({ block: "nearest" });
        } else if (e.key === "Enter" && i >= 0) { e.preventDefault(); pick(e.target, opts[i].getAttribute("data-value")); }
        else if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); closeSuggest(); }
    });

    // Suggestion fields get their positioning wrapper up front: moving a focused field would blur it.
    function wrapSuggest(root) {
        $$("[data-suggest]", root).forEach(function (input) {
            if (input.closest(".inc-suggest")) return;
            var wrap = doc.createElement("div");
            wrap.className = "inc-suggest";
            input.parentNode.insertBefore(wrap, input);
            wrap.appendChild(input);
        });
    }

    // ---------------------------------------------------------------- start
    wrapSuggest(form);
    $$("[data-entry]", form).forEach(wire);
    renumberAll();
    syncPresent();
    preparedMode();
})();
