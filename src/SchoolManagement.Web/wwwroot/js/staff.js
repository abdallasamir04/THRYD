/*
 * Staff, Departments and Management Team behaviour. Runs on page load and on every modal / list
 * that app.js fills (the "app:enhance" event). Display only: the server validates every save.
 *
 *  - Staff form: the subcategory and subject dropdowns follow the chosen department
 *  - <select data-autosubmit>: saves as soon as a new value is chosen (task status)
 *  - Date / time pickers (.picker): open the calendar or clock on click, quick-pick buttons
 */
(function () {
    "use strict";

    function $$(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }

    function revalidate(field) {
        if (!window.jQuery || !field.form) return;
        var $form = jQuery(field.form);
        if ($form.data("validator") && field.closest("[hidden]") === null) jQuery(field).valid();
    }

    function fill(select, values, current) {
        while (select.options.length > 1) select.remove(1);
        values.forEach(function (v) {
            var o = document.createElement("option");
            o.value = v;
            o.textContent = v;
            if (current && v.toLowerCase() === current.toLowerCase()) o.selected = true;
            select.appendChild(o);
        });
        // A single choice (Christian → Christian Religion) is selected for the user.
        if (values.length === 1) select.value = values[0];
        if (values.indexOf(select.value) < 0) select.value = "";
    }

    // ---------------------------------------------------------------- staff form: department → subcategory → subject
    function initDeptPicker(box) {
        if (box._deptReady) return;
        box._deptReady = true;
        var dept = box.querySelector("[data-dept-select]");
        var subField = box.querySelector("[data-subcat-field]");
        var sub = box.querySelector("[data-subcat-select]");
        var subLabel = box.querySelector("[data-subcat-label]");
        var subjectField = box.querySelector("[data-subject-field]");
        var subject = box.querySelector("[data-subject-select]");
        var subjectLabel = box.querySelector("[data-subject-label]");
        if (!dept || !sub || !subject) return;

        function rules() {
            var o = dept.options[dept.selectedIndex];
            var raw = o && o.getAttribute("data-subcats");
            if (!raw) return null;
            try { return JSON.parse(raw); } catch (e) { return null; }
        }

        function syncSubjects(r) {
            var chosen = r && r.options.filter(function (o) { return o.value === sub.value; })[0];
            var subjects = chosen ? chosen.subjects : [];
            fill(subject, subjects, subject.value);
            subjectField.hidden = subjects.length === 0;
            if (subjects.length === 0) subject.value = "";
            if (r && subjectLabel) subjectLabel.textContent = r.subjectLabel || "Subject";
        }

        function syncDept() {
            var r = rules();
            subField.hidden = !r;
            if (!r) {
                fill(sub, [], "");
                syncSubjects(null);
                return;
            }
            if (subLabel) subLabel.textContent = r.label;
            fill(sub, r.options.map(function (o) { return o.value; }), sub.value);
            syncSubjects(r);
        }

        dept.addEventListener("change", syncDept);
        sub.addEventListener("change", function () { syncSubjects(rules()); revalidate(sub); });
        subject.addEventListener("change", function () { revalidate(subject); });
        // The server already rendered the right fields; this only re-applies them (e.g. after validation).
        syncDept();
    }

    // ---------------------------------------------------------------- auto-submitting selects (task status)
    document.addEventListener("change", function (e) {
        var select = e.target.closest("select[data-autosubmit]");
        if (!select || !select.form) return;
        if (select.form.requestSubmit) select.form.requestSubmit();
        else select.form.dispatchEvent(new Event("submit", { cancelable: true, bubbles: true }));
    });

    // ---------------------------------------------------------------- date / time pickers
    // Quick-pick buttons keep focus where it is on mouse down: otherwise the blur re-validates the
    // previous field, its error message disappears, the layout shifts and the click misses the button.
    // Submit buttons too: after a server error, the blur would clear the error summary above the form and
    // the shifted layout made the first click on "Save" land beside the button.
    document.addEventListener("mousedown", function (e) {
        if (e.button === 0 && e.target.closest("[data-date-quick], form button[type=submit], button[type=submit][form]")) e.preventDefault();
    });

    function pad(n) { return (n < 10 ? "0" : "") + n; }
    function iso(d) { return d.getFullYear() + "-" + pad(d.getMonth() + 1) + "-" + pad(d.getDate()); }

    document.addEventListener("click", function (e) {
        // Clicking anywhere in the field opens the calendar / clock (typing still works).
        var input = e.target.closest("input.picker");
        if (input && !input.disabled && !input.readOnly && typeof input.showPicker === "function") {
            try { input.showPicker(); } catch (err) { /* not allowed here (e.g. inside a cross-origin frame) */ }
            return;
        }

        var quick = e.target.closest("[data-date-quick]");
        if (!quick) return;
        e.preventDefault();
        var form = quick.closest("form") || document;
        var target = form.querySelector(quick.getAttribute("data-target"));
        if (!target) return;
        var value = quick.getAttribute("data-date-quick");
        if (value === "clear") {
            target.value = "";
            // Clearing the date also clears a time that depends on it.
            var linked = target.getAttribute("data-clears") && form.querySelector(target.getAttribute("data-clears"));
            if (linked) { linked.value = ""; linked.dispatchEvent(new Event("change", { bubbles: true })); }
        } else {
            var d = new Date();
            d.setDate(d.getDate() + parseInt(value, 10));
            target.value = iso(d);
        }
        target.dispatchEvent(new Event("change", { bubbles: true }));
        revalidate(target);
    });

    // A time needs a date: the time field is enabled only once a date is chosen.
    function initTimeLinks(root) {
        $$("input[data-needs]", root).forEach(function (time) {
            if (time._linked) return;
            time._linked = true;
            var form = time.closest("form");
            var date = form && form.querySelector(time.getAttribute("data-needs"));
            if (!date) return;
            var sync = function () {
                time.disabled = !date.value;
                if (!date.value) time.value = "";
                var hint = form.querySelector("[data-time-hint]");
                if (hint) hint.hidden = !!date.value;
            };
            date.addEventListener("change", sync);
            date.addEventListener("input", sync);
            sync();
        });
    }

    // ---------------------------------------------------------------- Assign Period: department → subject → teacher
    function esc(s) { var d = document.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }

    // Shows the chosen teacher's department, weekly workload and availability (from the option's data-info).
    function showTeacherInfo(select) {
        var form = select.closest("form");
        var box = form && form.querySelector("[data-ap-info]");
        if (!box) return;
        var o = select.options[select.selectedIndex];
        var raw = o && o.getAttribute("data-info");
        if (!raw) { box.hidden = true; box.innerHTML = ""; return; }
        var i;
        try { i = JSON.parse(raw); } catch (e) { box.hidden = true; return; }
        box.hidden = false;
        box.className = "teacher-info teacher-info--" + (i.Available ? i.status : "full");
        box.innerHTML =
            '<div><span class="slot-facts__label">Department</span><strong>' + esc(i.Department || "—") + "</strong></div>" +
            '<div><span class="slot-facts__label">This week</span><strong>' + i.Total + " / " + i.Max + "</strong></div>" +
            '<div><span class="slot-facts__label">Remaining</span><strong>' + i.Remaining + "</strong></div>" +
            '<div><span class="slot-facts__label">Workload</span><span class="wl-status wl-status--' + esc(i.status) + '">' + esc(i.statusLabel) + "</span></div>" +
            '<div class="teacher-info__avail"><span class="slot-facts__label">This slot</span><strong>' +
            (i.Available ? "Available" : esc(i.Reason || "Not available")) + "</strong></div>";
    }

    function initAssignPeriod(form) {
        if (form._apReady) return;
        form._apReady = true;
        var dept = form.querySelector("[data-ap-dept]");
        var subject = form.querySelector("[data-ap-subject]");
        var teacher = form.querySelector("[data-ap-teacher]");
        if (!dept || !subject || !teacher) return;
        var seq = 0;

        function setTeachers(items, keep) {
            while (teacher.options.length > 0) teacher.remove(0);
            var first = document.createElement("option");
            first.value = "";
            first.textContent = items.length ? "Choose a teacher…" : "No eligible teachers for this subject";
            teacher.appendChild(first);
            items.forEach(function (c) {
                var o = document.createElement("option");
                o.value = c.id;
                o.textContent = c.name + " — " + c.total + "/" + c.max + (c.available ? "" : " · unavailable");
                o.disabled = !c.available && String(c.id) !== keep;
                if (c.reason) o.title = c.reason;
                o.setAttribute("data-info", JSON.stringify({
                    Department: c.department, Total: c.total, Max: c.max, Remaining: c.remaining, Available: c.available,
                    Reason: c.reason, status: c.status, statusLabel: c.statusLabel
                }));
                if (String(c.id) === keep) o.selected = true;
                teacher.appendChild(o);
            });
            teacher.disabled = false;
            showTeacherInfo(teacher);
        }

        function loadTeachers() {
            var keep = teacher.value;
            if (!dept.value || !subject.value) {
                while (teacher.options.length > 1) teacher.remove(1);
                teacher.options[0].textContent = dept.value ? "Choose a subject first" : "Choose a department first";
                teacher.value = "";
                teacher.disabled = true;
                showTeacherInfo(teacher);
                return;
            }
            var qs = "departmentId=" + encodeURIComponent(dept.value) + "&subject=" + encodeURIComponent(subject.value) +
                "&day=" + encodeURIComponent(form.getAttribute("data-day")) + "&slotId=" + encodeURIComponent(form.getAttribute("data-slot")) +
                (form.getAttribute("data-exclude") ? "&excludeSessionId=" + form.getAttribute("data-exclude") : "");
            var mine = ++seq;
            var sk = window.App && App.skeleton;
            var info = form.querySelector("[data-ap-info]");
            teacher.disabled = true;
            teacher.options[0].textContent = "Loading teachers…";
            if (sk) { sk.field(teacher, true); if (info && !info.hidden) sk.field(info, true); }
            var done = function () { if (sk) { sk.field(teacher, false); sk.field(info, false); } };
            fetch("/timetable/candidates?" + qs, { headers: { "X-Requested-With": "XMLHttpRequest" } })
                .then(function (r) { if (!r.ok) throw new Error(String(r.status)); return r.json(); })
                .then(function (items) { if (mine === seq) { done(); setTeachers(items, keep); } })
                .catch(function () {
                    if (mine !== seq) return;
                    done();
                    teacher.options[0].textContent = "Could not load teachers — choose the subject again";
                    if (window.App) App.toast("Could not load the teachers. Check the connection.", "error");
                });
        }

        dept.addEventListener("change", function () {
            var o = dept.options[dept.selectedIndex];
            var list = [];
            try { list = JSON.parse((o && o.getAttribute("data-subjects")) || "[]"); } catch (e) { list = []; }
            fill(subject, list, subject.value);
            subject.options[0].textContent = dept.value ? "Choose a subject…" : "Choose a department first";
            subject.disabled = !dept.value;
            revalidate(dept);
            loadTeachers();
        });
        subject.addEventListener("change", function () { revalidate(subject); loadTeachers(); });
        teacher.addEventListener("change", function () { showTeacherInfo(teacher); revalidate(teacher); });
        showTeacherInfo(teacher);
    }

    // Other forms with a teacher list carrying data-info (substitute).
    document.addEventListener("change", function (e) {
        var s = e.target.closest("form[data-teacher-picker] [data-ap-teacher]");
        if (s) showTeacherInfo(s);

        // Office hours / duty panels on a teacher's slot.
        var kind = e.target.closest("[data-kind]");
        if (kind) {
            var form = kind.closest("form");
            $$("[data-kind-panel]", form).forEach(function (p) { p.hidden = p.getAttribute("data-kind-panel") !== kind.value; });
        }

        // Timetable switcher: go to the chosen class / teacher, same week.
        var nav = e.target.closest("select[data-navigate]");
        if (nav && nav.value) {
            var week = nav.getAttribute("data-week");
            window.location.href = nav.getAttribute("data-navigate") + encodeURIComponent(nav.value) + (week ? "?week=" + encodeURIComponent(week) : "");
        }
    });

    // P.E. equipment checklist: add / remove rows (saved with the details form).
    function addEquipment(box) {
        var input = box.querySelector("[data-equipment-new]");
        var name = (input.value || "").trim();
        if (!name) { input.focus(); return; }
        var key = "n" + Date.now() + Math.floor(Math.random() * 1000);
        var li = document.createElement("li");
        li.className = "pe-equip__row";
        li.innerHTML = '<input type="checkbox" name="Equipment[' + key + '].Checked" value="true" aria-label="Ready" />' +
            '<input class="input input--sm" name="Equipment[' + key + '].Name" maxlength="100" dir="auto" aria-label="Equipment item" />' +
            '<button class="icon-btn icon-btn--danger" type="button" data-equipment-remove aria-label="Remove item">' +
            '<svg class="icon" aria-hidden="true"><use href="#i-x"></use></svg></button>';
        li.querySelector("input[type=text], input:not([type])").value = name;
        box.querySelector("[data-equipment-list]").appendChild(li);
        var empty = box.querySelector("[data-equipment-empty]");
        if (empty) empty.hidden = true;
        input.value = "";
        input.focus();
    }
    document.addEventListener("click", function (e) {
        var add = e.target.closest("[data-equipment-add]");
        if (add) { addEquipment(add.closest("[data-equipment]")); return; }
        var rm = e.target.closest("[data-equipment-remove]");
        if (rm) {
            var box = rm.closest("[data-equipment]");
            rm.closest("li").remove();
            var empty = box.querySelector("[data-equipment-empty]");
            if (empty) empty.hidden = box.querySelectorAll(".pe-equip__row").length > 0;
        }
    });
    document.addEventListener("keydown", function (e) {
        // Enter in "add item" adds the item instead of submitting the form.
        if (e.key === "Enter" && e.target.matches("[data-equipment-new]")) { e.preventDefault(); addEquipment(e.target.closest("[data-equipment]")); }
    });

    // Belongings: the Subject list follows the chosen Department (subjects come from the server with each department).
    document.addEventListener("change", function (e) {
        var dept = e.target.closest("select[data-subject-source]");
        if (!dept) return;
        var target = dept.form && dept.form.querySelector("[data-subject-target]");
        if (!target) return;
        var o = dept.options[dept.selectedIndex];
        var list = [];
        try { list = JSON.parse((o && o.getAttribute("data-subjects")) || "[]"); } catch (err) { list = []; }
        fill(target, list, "");
        target.options[0].textContent = dept.value ? "Choose a subject…" : "Choose a department first";
        target.disabled = !dept.value;
    });

    // Instant filter for card grids (class picker).
    document.addEventListener("input", function (e) {
        var input = e.target.closest("input[data-filter-cards]");
        if (!input) return;
        var grid = document.querySelector(input.getAttribute("data-filter-cards"));
        var term = input.value.trim().toLowerCase();
        $$("[data-card-text]", grid).forEach(function (card) {
            card.hidden = term !== "" && card.getAttribute("data-card-text").toLowerCase().indexOf(term) < 0;
        });
    });

    function init(root) {
        $$("[data-dept-picker]", root).forEach(initDeptPicker);
        $$("form[data-assign-period]", root).forEach(initAssignPeriod);
        $$("form[data-teacher-picker] [data-ap-teacher]", root).forEach(showTeacherInfo);
        initTimeLinks(root);
    }

    document.addEventListener("app:enhance", function (e) { init(e.detail || document); });
})();
