/*
 * Management Team task form (runs on page load and on every modal app.js fills, via "app:enhance").
 * Display only: the server validates every value and every file.
 *
 *  - <input type="date" data-datepicker>: THRYD calendar (month navigation, Today, Clear, min / max). The input
 *    becomes hidden and keeps posting an ISO date (yyyy-mm-dd).
 *  - <input type="time" data-timepicker>: 12-hour picker (hour, minute, AM / PM). The input keeps posting a
 *    24-hour time (HH:mm), so 12- and 24-hour values are never mixed.
 *  - [data-task-files]: drag-and-drop / browse uploads, one request per file with progress; each upload returns a
 *    key that the form posts (AttachmentKeys). Saved files can be marked for removal (RemoveAttachmentIds).
 *  - [data-due-summary]: the chosen deadline in words.
 * "Today" is the school date (Africa/Cairo, from SchoolClock), not the browser's.
 */
(function () {
    "use strict";

    var doc = document;
    var MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    var DAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    function $$(sel, root) { return Array.prototype.slice.call((root || doc).querySelectorAll(sel)); }
    function pad(n) { return (n < 10 ? "0" : "") + n; }
    function icon(name, cls) { return '<svg class="icon ' + (cls || "") + '" aria-hidden="true"><use href="#i-' + name + '"></use></svg>'; }
    function esc(s) { var d = doc.createElement("div"); d.textContent = s == null ? "" : String(s); return d.innerHTML; }

    // Dates are handled as plain calendar days ("yyyy-mm-dd") in UTC, so no browser time zone or DST can shift them.
    function parseIso(s) {
        var m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(s || "");
        return m ? new Date(Date.UTC(+m[1], +m[2] - 1, +m[3])) : null;
    }
    function iso(d) { return d.getUTCFullYear() + "-" + pad(d.getUTCMonth() + 1) + "-" + pad(d.getUTCDate()); }
    function addDays(d, n) { return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate() + n)); }
    function todayIso() {
        if (window.SchoolClock) return window.SchoolClock.date(0);
        var n = new Date();
        return n.getFullYear() + "-" + pad(n.getMonth() + 1) + "-" + pad(n.getDate());
    }
    function longDate(d) { return DAYS[d.getUTCDay()].slice(0, 3) + ", " + d.getUTCDate() + " " + MONTHS[d.getUTCMonth()].slice(0, 3) + " " + d.getUTCFullYear(); }

    function to12(hhmm) {
        var m = /^(\d{1,2}):(\d{2})/.exec(hhmm || "");
        if (!m) return null;
        var h = +m[1], mi = +m[2];
        return { hour: h % 12 === 0 ? 12 : h % 12, minute: mi, pm: h >= 12 };
    }
    function to24(t) { return pad((t.hour % 12) + (t.pm ? 12 : 0)) + ":" + pad(t.minute); }
    function label12(t) { return t.hour + ":" + pad(t.minute) + " " + (t.pm ? "PM" : "AM"); }

    // One open popover at a time; a click outside or Escape closes it.
    var openPanel = null;
    function closeOpen(focusButton) {
        if (!openPanel) return;
        var p = openPanel;
        openPanel = null;
        p.close(focusButton);
    }
    doc.addEventListener("mousedown", function (e) {
        if (openPanel && !openPanel.root.contains(e.target)) closeOpen(false);
    }, true);

    function placePanel(root, panel) {
        var rect = root.getBoundingClientRect();
        panel.classList.toggle("is-up", window.innerHeight - rect.bottom < 380 && rect.top > 380);
    }

    // ---------------------------------------------------------------- date picker
    function initDate(input) {
        if (input._dp) return;
        var form = input.form;
        var min = parseIso(input.getAttribute("min"));
        var max = parseIso(input.getAttribute("max"));
        var emptyText = input.getAttribute("data-empty-text") || "No date";
        var id = input.id || ("dp-" + Math.random().toString(36).slice(2));

        var root = doc.createElement("div");
        root.className = "dp";
        input.parentNode.insertBefore(root, input);
        root.appendChild(input);
        input.type = "hidden";
        input.classList.remove("picker", "input");

        var button = doc.createElement("button");
        button.type = "button";
        button.className = "input dp__button";
        button.id = id + "-btn";
        button.setAttribute("aria-haspopup", "dialog");
        button.setAttribute("aria-expanded", "false");
        button.innerHTML = icon("calendar") + '<span class="dp__value"></span>' + icon("chevron-down", "dp__chevron");
        root.appendChild(button);
        var label = form && form.querySelector('label[for="' + input.id + '"]');
        if (label) { label.setAttribute("for", button.id); label.id = label.id || id + "-label"; button.setAttribute("aria-labelledby", label.id + " " + id + "-value"); }
        button.querySelector(".dp__value").id = id + "-value";

        var panel = null, view = null, focusDay = null;

        function value() { return parseIso(input.value); }
        function sync() {
            var v = value();
            var span = button.querySelector(".dp__value");
            span.textContent = v ? longDate(v) : emptyText;
            span.classList.toggle("is-placeholder", !v);
        }
        function inRange(d) { return (!min || d >= min) && (!max || d <= max); }
        function set(d) {
            input.value = d ? iso(d) : "";
            sync();
            input.dispatchEvent(new Event("change", { bubbles: true }));
            if (!d) {
                // Clearing the date also clears a time that depends on it (same rule as the quick "No due date").
                var linked = input.getAttribute("data-clears") && form && form.querySelector(input.getAttribute("data-clears"));
                if (linked && linked.value) { linked.value = ""; linked.dispatchEvent(new Event("change", { bubbles: true })); }
            }
        }

        function render() {
            var y = view.getUTCFullYear(), mo = view.getUTCMonth();
            var first = new Date(Date.UTC(y, mo, 1));
            var start = addDays(first, -first.getUTCDay()); // weeks start on Sunday (the school week)
            var today = todayIso(), selected = input.value;
            var prevOk = !min || new Date(Date.UTC(y, mo, 0)) >= min;
            var nextOk = !max || new Date(Date.UTC(y, mo + 1, 1)) <= max;
            var html = '<div class="dp__head">' +
                '<button type="button" class="icon-btn dp__nav" data-dp-move="-1" aria-label="Previous month"' + (prevOk ? "" : " disabled") + ">" + icon("chevron-left") + "</button>" +
                '<span class="dp__month" aria-live="polite">' + MONTHS[mo] + " " + y + "</span>" +
                '<button type="button" class="icon-btn dp__nav" data-dp-move="1" aria-label="Next month"' + (nextOk ? "" : " disabled") + ">" + icon("chevron-right") + "</button></div>" +
                '<div class="dp__grid" role="grid" aria-label="' + MONTHS[mo] + " " + y + '"><div class="dp__row" role="row">';
            DAYS.forEach(function (d) { html += '<span class="dp__dow" role="columnheader" aria-label="' + d + '">' + d.slice(0, 2) + "</span>"; });
            html += "</div>";
            for (var w = 0; w < 6; w++) {
                html += '<div class="dp__row" role="row">';
                for (var i = 0; i < 7; i++) {
                    var day = addDays(start, w * 7 + i), key = iso(day);
                    var cls = "dp__day" + (day.getUTCMonth() !== mo ? " is-outside" : "") + (key === today ? " is-today" : "") + (key === selected ? " is-selected" : "") +
                        (i === 5 || i === 6 ? " is-weekend" : "");
                    html += '<button type="button" role="gridcell" class="' + cls + '" data-dp-day="' + key + '" tabindex="-1"' +
                        ' aria-label="' + DAYS[day.getUTCDay()] + " " + day.getUTCDate() + " " + MONTHS[day.getUTCMonth()] + " " + day.getUTCFullYear() + (key === today ? ", today" : "") + '"' +
                        (key === selected ? ' aria-selected="true"' : "") + (inRange(day) ? "" : " disabled") + ">" + day.getUTCDate() + "</button>";
                }
                html += "</div>";
            }
            html += '</div><div class="dp__foot">' +
                '<button type="button" class="btn btn--ghost btn--sm" data-dp-today>Today</button>' +
                '<button type="button" class="btn btn--ghost btn--sm" data-dp-clear>' + esc(emptyText) + "</button></div>";
            panel.innerHTML = html;
            var target = panel.querySelector('[data-dp-day="' + iso(focusDay) + '"]:not([disabled])') ||
                panel.querySelector(".dp__day.is-selected:not([disabled])") || panel.querySelector(".dp__day:not(.is-outside):not([disabled])");
            if (target) target.tabIndex = 0;
            return target;
        }
        function focusOn(d) {
            if (min && d < min) d = min;
            if (max && d > max) d = max;
            focusDay = d;
            if (d.getUTCMonth() !== view.getUTCMonth() || d.getUTCFullYear() !== view.getUTCFullYear()) view = new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), 1));
            var t = render();
            if (t) t.focus();
        }
        function open() {
            if (panel) return;
            closeOpen(false);
            var v = value(), today = parseIso(todayIso());
            focusDay = v || (min && today < min ? min : today);
            view = new Date(Date.UTC(focusDay.getUTCFullYear(), focusDay.getUTCMonth(), 1));
            panel = doc.createElement("div");
            panel.className = "dp__panel";
            panel.setAttribute("role", "dialog");
            panel.setAttribute("aria-label", "Choose a date");
            root.appendChild(panel);
            placePanel(root, panel);
            var t = render();
            button.setAttribute("aria-expanded", "true");
            root.classList.add("is-open");
            if (t) t.focus();
            panel.addEventListener("click", onClick);
            panel.addEventListener("keydown", onKey);
            openPanel = { root: root, close: close };
        }
        function close(focusButton) {
            if (!panel) return;
            panel.remove();
            panel = null;
            root.classList.remove("is-open");
            button.setAttribute("aria-expanded", "false");
            if (openPanel && openPanel.root === root) openPanel = null;
            if (focusButton) button.focus();
        }
        function onClick(e) {
            var b = e.target.closest("button");
            if (!b || b.disabled) return;
            if (b.hasAttribute("data-dp-move")) {
                view = new Date(Date.UTC(view.getUTCFullYear(), view.getUTCMonth() + +b.getAttribute("data-dp-move"), 1));
                focusDay = view;
                render();
                return;
            }
            if (b.hasAttribute("data-dp-today")) { var t = parseIso(todayIso()); if (inRange(t)) { set(t); close(true); } else focusOn(t); return; }
            if (b.hasAttribute("data-dp-clear")) { set(null); close(true); return; }
            if (b.hasAttribute("data-dp-day")) { set(parseIso(b.getAttribute("data-dp-day"))); close(true); }
        }
        function onKey(e) {
            var day = e.target.getAttribute && e.target.getAttribute("data-dp-day");
            if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); close(true); return; }
            if (!day) return;
            var d = parseIso(day), step = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -7, ArrowDown: 7 }[e.key];
            if (step) { e.preventDefault(); focusOn(addDays(d, step)); }
            else if (e.key === "PageUp" || e.key === "PageDown") {
                e.preventDefault();
                focusOn(new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + (e.key === "PageUp" ? -1 : 1), Math.min(d.getUTCDate(), 28))));
            }
            else if (e.key === "Home") { e.preventDefault(); focusOn(addDays(d, -d.getUTCDay())); }
            else if (e.key === "End") { e.preventDefault(); focusOn(addDays(d, 6 - d.getUTCDay())); }
        }

        button.addEventListener("click", function () { panel ? close(true) : open(); });
        button.addEventListener("keydown", function (e) { if (e.key === "ArrowDown") { e.preventDefault(); open(); } });
        // Quick buttons (staff.js) set the value and send "change": show it.
        input.addEventListener("change", sync);
        input._dp = { sync: sync };
        sync();
    }

    // ---------------------------------------------------------------- time picker (12-hour)
    function initTime(input) {
        if (input._tp) return;
        var form = input.form;
        var id = input.id || ("tp-" + Math.random().toString(36).slice(2));
        var root = doc.createElement("div");
        root.className = "tp";
        input.parentNode.insertBefore(root, input);
        root.appendChild(input);
        input.type = "hidden";
        input.classList.remove("picker", "input");

        var button = doc.createElement("button");
        button.type = "button";
        button.className = "input dp__button tp__button";
        button.id = id + "-btn";
        button.setAttribute("aria-haspopup", "dialog");
        button.setAttribute("aria-expanded", "false");
        button.innerHTML = icon("clock") + '<span class="dp__value" id="' + id + '-value"></span>' + icon("chevron-down", "dp__chevron");
        root.appendChild(button);
        var label = form && form.querySelector('label[for="' + input.id + '"]');
        if (label) { label.setAttribute("for", button.id); label.id = label.id || id + "-label"; button.setAttribute("aria-labelledby", label.id + " " + id + "-value"); }

        var panel = null;
        function current() { return to12(input.value); }
        function sync() {
            var t = current();
            var span = button.querySelector(".dp__value");
            span.textContent = t ? label12(t) : "No time (end of the day)";
            span.classList.toggle("is-placeholder", !t);
            button.disabled = input.disabled;
            root.classList.toggle("is-disabled", input.disabled);
        }
        function set(t) {
            input.value = t ? to24(t) : "";
            sync();
            input.dispatchEvent(new Event("change", { bubbles: true }));
            if (panel) render();
        }
        function column(name, items, selected) {
            var html = '<div class="tp__col" role="listbox" aria-label="' + name + '">';
            items.forEach(function (it) {
                html += '<button type="button" role="option" class="tp__opt' + (it.value === selected ? " is-selected" : "") + '" data-tp-' + name.toLowerCase() + '="' + it.value + '"' +
                    (it.value === selected ? ' aria-selected="true"' : ' aria-selected="false"') + ">" + it.label + "</button>";
            });
            return html + "</div>";
        }
        function render() {
            var t = current();
            var hours = [], minutes = [];
            for (var h = 1; h <= 12; h++) hours.push({ value: String(h), label: String(h) });
            for (var m = 0; m < 60; m += 5) minutes.push({ value: String(m), label: pad(m) });
            if (t && t.minute % 5 !== 0) { minutes.push({ value: String(t.minute), label: pad(t.minute) }); minutes.sort(function (a, b) { return a.value - b.value; }); }
            panel.innerHTML = '<div class="tp__head"><span class="tp__now">' + (t ? esc(label12(t)) : "Choose a time") + '</span><span class="hint">Cairo time</span></div>' +
                '<div class="tp__cols">' +
                column("Hour", hours, t ? String(t.hour) : null) +
                column("Minute", minutes, t ? String(t.minute) : null) +
                column("Period", [{ value: "AM", label: "AM" }, { value: "PM", label: "PM" }], t ? (t.pm ? "PM" : "AM") : null) +
                "</div>" +
                '<div class="tp__quick">' +
                ['8:00 AM', '12:00 PM', '2:00 PM', '5:00 PM'].map(function (q) { return '<button type="button" class="tp__chip" data-tp-quick="' + q + '">' + q + "</button>"; }).join("") +
                '</div><div class="dp__foot"><button type="button" class="btn btn--ghost btn--sm" data-tp-clear>No time</button>' +
                '<button type="button" class="btn btn--primary btn--sm" data-tp-done>Done</button></div>';
            $$(".tp__opt.is-selected", panel).forEach(function (o) { o.scrollIntoView({ block: "center" }); });
        }
        function defaultsFor(hour) {
            // A school deadline: 7–11 is morning, 12 and 1–6 afternoon; the user can switch AM / PM.
            return { hour: hour, minute: 0, pm: hour === 12 || hour <= 6 };
        }
        function open() {
            if (panel || input.disabled) return;
            closeOpen(false);
            panel = doc.createElement("div");
            panel.className = "dp__panel tp__panel";
            panel.setAttribute("role", "dialog");
            panel.setAttribute("aria-label", "Choose a time");
            root.appendChild(panel);
            placePanel(root, panel);
            render();
            button.setAttribute("aria-expanded", "true");
            root.classList.add("is-open");
            var first = panel.querySelector(".tp__opt.is-selected") || panel.querySelector(".tp__opt");
            if (first) first.focus();
            panel.addEventListener("click", onClick);
            panel.addEventListener("keydown", onKey);
            openPanel = { root: root, close: close };
        }
        function close(focusButton) {
            if (!panel) return;
            panel.remove();
            panel = null;
            root.classList.remove("is-open");
            button.setAttribute("aria-expanded", "false");
            if (openPanel && openPanel.root === root) openPanel = null;
            if (focusButton) button.focus();
        }
        function onClick(e) {
            var b = e.target.closest("button");
            if (!b) return;
            var t = current();
            if (b.hasAttribute("data-tp-hour")) { var h = +b.getAttribute("data-tp-hour"); set(t ? { hour: h, minute: t.minute, pm: t.pm } : defaultsFor(h)); refocus("hour", h); }
            else if (b.hasAttribute("data-tp-minute")) { var m = +b.getAttribute("data-tp-minute"); set({ hour: t ? t.hour : 12, minute: m, pm: t ? t.pm : true }); refocus("minute", m); }
            else if (b.hasAttribute("data-tp-period")) { var pm = b.getAttribute("data-tp-period") === "PM"; set({ hour: t ? t.hour : 12, minute: t ? t.minute : 0, pm: pm }); refocus("period", pm ? "PM" : "AM"); }
            else if (b.hasAttribute("data-tp-quick")) { var q = /^(\d+):(\d+) (AM|PM)$/.exec(b.getAttribute("data-tp-quick")); set({ hour: +q[1], minute: +q[2], pm: q[3] === "PM" }); close(true); }
            else if (b.hasAttribute("data-tp-clear")) { set(null); close(true); }
            else if (b.hasAttribute("data-tp-done")) close(true);
        }
        function refocus(kind, v) { var o = panel && panel.querySelector("[data-tp-" + kind + '="' + v + '"]'); if (o) o.focus(); }
        function onKey(e) {
            if (e.key === "Escape") { e.preventDefault(); e.stopPropagation(); close(true); return; }
            var opt = e.target.closest && e.target.closest(".tp__opt");
            if (!opt) return;
            var list = $$(".tp__opt", opt.parentNode), i = list.indexOf(opt);
            if (e.key === "ArrowDown" || e.key === "ArrowUp") { e.preventDefault(); var n = list[i + (e.key === "ArrowDown" ? 1 : -1)]; if (n) n.focus(); }
            else if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
                e.preventDefault();
                var cols = $$(".tp__col", panel), c = cols.indexOf(opt.parentNode) + (e.key === "ArrowRight" ? 1 : -1);
                if (cols[c]) (cols[c].querySelector(".is-selected") || cols[c].querySelector(".tp__opt")).focus();
            }
        }

        button.addEventListener("click", function () { panel ? close(true) : open(); });
        input.addEventListener("change", sync);
        // The due date enables / clears the time (staff.js): follow it.
        var date = form && input.getAttribute("data-needs") && form.querySelector(input.getAttribute("data-needs"));
        if (date) date.addEventListener("change", function () { setTimeout(sync, 0); });
        input._tp = { sync: sync };
        setTimeout(sync, 0);
    }

    // ---------------------------------------------------------------- due summary
    function initSummary(form) {
        var box = form.querySelector("[data-due-summary-text]");
        var date = form.querySelector("#DueDate"), time = form.querySelector("#DueTime");
        if (!box || !date || form._dueSummary) return;
        form._dueSummary = true;
        var update = function () {
            var d = parseIso(date.value), t = time ? to12(time.value) : null;
            box.textContent = !d ? "No due date — the task stays open until it is completed."
                : "Due " + longDate(d) + (t ? " at " + label12(t) : " (end of the day)") + ", Cairo time.";
        };
        date.addEventListener("change", update);
        if (time) time.addEventListener("change", update);
        update();
    }

    // ---------------------------------------------------------------- attachments
    var KINDS = { pdf: ["PDF", "file-pdf"], xlsx: ["Excel", "sheet"], xls: ["Excel", "sheet"], docx: ["Word", "file-text"], doc: ["Word", "file-text"],
        pptx: ["PowerPoint", "presentation"], ppt: ["PowerPoint", "presentation"], png: ["Image", "image"], jpg: ["Image", "image"], jpeg: ["Image", "image"] };
    function sizeLabel(b) { return b < 1024 ? b + " B" : b < 1048576 ? Math.max(1, Math.round(b / 1024)) + " KB" : (b / 1048576).toFixed(1).replace(/\.0$/, "") + " MB"; }

    function initFiles(box) {
        if (box._files) return;
        box._files = true;
        var form = box.closest("form");
        var list = box.querySelector("[data-task-file-list]");
        var empty = box.querySelector("[data-task-files-empty]");
        var input = box.querySelector("[data-task-file-input]");
        var drop = box.querySelector("[data-task-drop]");
        var url = box.getAttribute("data-upload-url");
        var maxFiles = +box.getAttribute("data-max-files") || 10;
        var maxBytes = +box.getAttribute("data-max-bytes") || 15728640;
        var submit = form && form.querySelector("[data-task-submit]");
        var pending = 0;

        function count() { return $$(".task-file:not(.is-removed):not(.is-error)", list).length; }
        function refresh() {
            if (empty) empty.hidden = list.children.length > 0;
            if (submit) {
                submit.disabled = pending > 0;
                submit.title = pending > 0 ? "Wait until the files finish uploading" : "";
            }
        }
        function row(file, ext) {
            var kind = KINDS[ext] || ["File", "file"];
            var li = doc.createElement("li");
            li.className = "task-file is-uploading";
            li.innerHTML = '<span class="task-file__icon task-file__icon--' + esc(ext) + '">' + icon(kind[1]) + "</span>" +
                '<span class="task-file__body"><span class="task-file__name" dir="auto"></span>' +
                '<span class="task-file__meta">' + kind[0] + " · " + sizeLabel(file.size) + ' · <span data-file-state>Waiting…</span></span>' +
                '<span class="task-file__bar" aria-hidden="true"><span></span></span></span>' +
                '<button class="icon-btn icon-btn--danger" type="button" data-file-remove title="Remove">' + icon("x") + "</button>";
            li.querySelector(".task-file__name").textContent = file.name;
            li.querySelector("[data-file-remove]").setAttribute("aria-label", "Remove " + file.name);
            list.appendChild(li);
            return li;
        }
        function fail(li, message) {
            li.classList.remove("is-uploading");
            li.classList.add("is-error");
            li.querySelector("[data-file-state]").textContent = message;
            if (window.App) App.toast(message, "error");
        }
        function upload(file) {
            var ext = (file.name.split(".").pop() || "").toLowerCase();
            var li = row(file, ext);
            // Quick checks for instant feedback; the server checks type, content and size again.
            if (!KINDS[ext] || file.name.indexOf(".") < 0) { fail(li, "Not a PDF, Word, Excel, PowerPoint or PNG / JPG file."); refresh(); return; }
            if (file.size === 0) { fail(li, "This file is empty."); refresh(); return; }
            if (file.size > maxBytes) { fail(li, "Too large — at most " + sizeLabel(maxBytes) + "."); refresh(); return; }
            if (count() > maxFiles) { fail(li, "A task can have at most " + maxFiles + " attachments."); refresh(); return; }

            var data = new FormData();
            data.append("file", file);
            var token = form && form.querySelector("input[name=__RequestVerificationToken]");
            if (token) data.append("__RequestVerificationToken", token.value);
            var xhr = new XMLHttpRequest();
            li._xhr = xhr;
            pending++;
            refresh();
            var state = li.querySelector("[data-file-state]"), bar = li.querySelector(".task-file__bar span");
            state.textContent = "Uploading…";
            xhr.upload.addEventListener("progress", function (e) {
                if (!e.lengthComputable) return;
                var p = Math.round(e.loaded / e.total * 100);
                bar.style.width = p + "%";
                state.textContent = "Uploading… " + p + "%";
            });
            xhr.addEventListener("loadend", function () {
                pending--;
                li._xhr = null;
                var res = null;
                try { res = JSON.parse(xhr.responseText); } catch (err) { /* not JSON */ }
                if (xhr.status === 200 && res && res.success) {
                    li.classList.remove("is-uploading");
                    li.classList.add("is-uploaded");
                    li.querySelector(".task-file__name").textContent = res.name;
                    state.textContent = "Uploaded — attached when you save";
                    var hidden = doc.createElement("input");
                    hidden.type = "hidden";
                    hidden.name = "AttachmentKeys";
                    hidden.value = res.key;
                    li.appendChild(hidden);
                    form.dispatchEvent(new Event("input", { bubbles: true })); // counts as an unsaved change
                } else if (xhr.status !== 0 || !li._aborted) {
                    fail(li, res && res.message ? res.message : xhr.status === 413 ? "Too large — at most " + sizeLabel(maxBytes) + "." : "The upload failed. Try again.");
                }
                refresh();
            });
            xhr.open("POST", url);
            xhr.setRequestHeader("X-Requested-With", "XMLHttpRequest");
            xhr.send(data);
        }
        function addFiles(files) { Array.prototype.slice.call(files || []).forEach(upload); }

        input.addEventListener("change", function () { addFiles(input.files); input.value = ""; });
        ["dragenter", "dragover"].forEach(function (t) {
            drop.addEventListener(t, function (e) { e.preventDefault(); drop.classList.add("is-over"); });
        });
        ["dragleave", "dragend"].forEach(function (t) { drop.addEventListener(t, function () { drop.classList.remove("is-over"); }); });
        drop.addEventListener("drop", function (e) {
            e.preventDefault();
            drop.classList.remove("is-over");
            if (e.dataTransfer) addFiles(e.dataTransfer.files);
        });
        drop.addEventListener("click", function (e) { if (!e.target.closest("label, input")) input.click(); });

        list.addEventListener("click", function (e) {
            var b = e.target.closest("[data-file-remove]");
            if (!b) return;
            var li = b.closest(".task-file");
            var savedId = li.getAttribute("data-saved-file");
            if (savedId) {
                // A saved file: toggle "remove on save".
                var hidden = li.querySelector('input[name="RemoveAttachmentIds"]');
                var name = li.querySelector(".task-file__name").textContent;
                if (hidden) { hidden.remove(); li.classList.remove("is-removed"); li.querySelector("[data-file-state]").textContent = "Saved"; b.innerHTML = icon("x"); b.title = "Remove"; b.setAttribute("aria-label", "Remove " + name); }
                else {
                    hidden = doc.createElement("input");
                    hidden.type = "hidden"; hidden.name = "RemoveAttachmentIds"; hidden.value = savedId;
                    li.appendChild(hidden);
                    li.classList.add("is-removed");
                    li.querySelector("[data-file-state]").textContent = "Will be removed on save";
                    b.innerHTML = icon("refresh"); b.title = "Keep"; b.setAttribute("aria-label", "Keep " + name);
                }
                form.dispatchEvent(new Event("input", { bubbles: true }));
            } else {
                if (li._xhr) { li._aborted = true; li._xhr.abort(); }
                li.remove();
            }
            refresh();
        });
        refresh();
    }

    function init(root) {
        $$("input[data-datepicker]", root).forEach(initDate);
        $$("input[data-timepicker]", root).forEach(initTime);
        $$("[data-task-files]", root).forEach(initFiles);
        $$("form[data-task-form]", root).forEach(initSummary);
        if (root.matches && root.matches("form[data-task-form]")) initSummary(root);
    }

    doc.addEventListener("keydown", function (e) { if (e.key === "Escape" && openPanel) { e.preventDefault(); closeOpen(true); } });
    doc.addEventListener("app:enhance", function (e) { init(e.detail || doc); });
})();
