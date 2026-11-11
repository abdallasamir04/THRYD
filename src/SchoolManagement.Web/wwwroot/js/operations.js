/*
 * Morning Duty and Teacher Workload form behaviour. Runs on every page load and on every modal
 * that app.js fills (the "app:enhance" event). Display only: the server validates every save.
 */
(function () {
    "use strict";

    function $$(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }

    // ---------------------------------------------------------------- duty form
    function initDutyForm(form) {
        if (form._dutyReady) return;
        form._dutyReady = true;
        var panel = form.querySelector("[data-duty-backup]");
        var backup = panel && panel.querySelector("select");
        var sync = function () {
            var checked = form.querySelector("[data-duty-attendance]:checked");
            var absent = checked && checked.value === "Absent";
            if (!panel) return;
            panel.hidden = !absent;
            if (!absent && backup && backup.value !== "") {
                backup.value = "";
                if (backup._combo) backup._combo.sync();
            }
        };
        $$("[data-duty-attendance]", form).forEach(function (r) { r.addEventListener("change", sync); });
        sync();
    }

    // ---------------------------------------------------------------- workload form
    function initWorkloadForm(form) {
        if (form._wlReady) return;
        form._wlReady = true;
        var teacher = form.querySelector("[data-wl-teacher]");
        var week = form.querySelector("[data-wl-week]");
        var periods = form.querySelector("[data-wl-periods]");
        var box = form.querySelector("[data-wl-capacity]");
        var duty = form.querySelector("[data-wl-duty]");
        var excludeId = form.getAttribute("data-exclude-id") || "";
        var state = null;

        function category() {
            var c = form.querySelector("[data-wl-category]:checked");
            return c ? c.value : "Class";
        }

        function showSections() {
            var cat = category();
            $$("[data-wl-show]", form).forEach(function (el) {
                var show = el.getAttribute("data-wl-show").split(" ").indexOf(cat) >= 0;
                el.hidden = !show;
            });
        }

        function paint() {
            if (!box) return;
            var msg = box.querySelector("[data-wl-message]");
            var adding = parseInt(periods && periods.value, 10) || 0;
            if (!state) {
                msg.textContent = teacher && teacher.value ? "" : "Choose a teacher to see their load.";
                return;
            }
            box.querySelector("[data-wl-total]").textContent = state.total;
            box.querySelector("[data-wl-remaining]").textContent = state.remaining;
            box.className = "capacity capacity--" + state.status;
            msg.className = "capacity__msg";
            var after = state.total + adding;
            if (state.remaining === 0) {
                msg.textContent = "Cannot assign this teacher. Their weekly workload is already " + state.max + "/" + state.max + " periods.";
            } else if (adding > state.remaining) {
                box.classList.add("is-over");
                msg.textContent = "Only " + state.remaining + " period" + (state.remaining === 1 ? "" : "s") + " can be added (" + state.total + "/" + state.max + " now).";
            } else if (adding > 0 && after === state.max) {
                msg.classList.add("is-warn");
                msg.textContent = "After this the teacher will be FULL (" + after + "/" + state.max + ").";
            } else if (adding > 0 && after >= 20) {
                msg.classList.add("is-warn");
                msg.textContent = "Approaching the maximum: " + after + "/" + state.max + " after this.";
            } else {
                msg.textContent = "";
            }
        }

        function load() {
            if (!teacher || !teacher.value || !week || !week.value) { state = null; paint(); return; }
            var qs = "teacherId=" + encodeURIComponent(teacher.value) + "&week=" + encodeURIComponent(week.value) + (excludeId ? "&excludeId=" + excludeId : "");
            var sk = window.App && App.skeleton;
            if (sk) sk.field(box, true); // capacity panel shimmers until the figures arrive
            fetch("/workload/capacity?" + qs, { headers: { "X-Requested-With": "XMLHttpRequest" } })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (data) {
                    state = data;
                    var label = form.querySelector("[data-wl-week-label]");
                    if (data && label) label.textContent = "Saved as the school week of " + data.weekLabel + ".";
                    paint();
                })
                .catch(function () { state = null; paint(); })
                .finally(function () { if (sk) sk.field(box, false); });
            if (duty) {
                var current = duty.value;
                if (sk) sk.field(duty, true);
                fetch("/workload/duty-options?" + qs.replace("excludeId", "ignore") + (current ? "&includeDutyId=" + current : ""), { headers: { "X-Requested-With": "XMLHttpRequest" } })
                    .then(function (r) { return r.ok ? r.json() : []; })
                    .then(function (items) {
                        while (duty.options.length > 1) duty.remove(1);
                        items.forEach(function (d) {
                            var o = document.createElement("option");
                            o.value = d.id;
                            o.textContent = d.label;
                            if (String(d.id) === current) o.selected = true;
                            duty.appendChild(o);
                        });
                    })
                    .catch(function () { if (window.App) App.toast("Could not load this teacher's duties. Check the connection.", "error"); })
                    .finally(function () { if (sk) sk.field(duty, false); });
            }
        }

        $$("[data-wl-category]", form).forEach(function (r) { r.addEventListener("change", showSections); });
        if (teacher) teacher.addEventListener("change", load);
        if (week) week.addEventListener("change", load);
        if (periods) periods.addEventListener("input", paint);
        showSections();
        load();
    }

    function init(root) {
        $$("form[data-duty-form]", root).forEach(initDutyForm);
        $$("form[data-workload-form]", root).forEach(initWorkloadForm);
    }

    document.addEventListener("app:enhance", function (e) { init(e.detail || document); });
})();
