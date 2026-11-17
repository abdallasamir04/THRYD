/*
 * Management Team task form (runs on page load and on every modal app.js fills, via "app:enhance").
 * Display only: the server validates every value and every file.
 *
 *  - Date and time fields use the shared THRYD pickers (js/thryd-ui.js).
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
    function longDate(d) { return DAYS[d.getUTCDay()].slice(0, 3) + ", " + d.getUTCDate() + " " + MONTHS[d.getUTCMonth()].slice(0, 3) + " " + d.getUTCFullYear(); }

    function to12(hhmm) {
        var m = /^(\d{1,2}):(\d{2})/.exec(hhmm || "");
        if (!m) return null;
        var h = +m[1], mi = +m[2];
        return { hour: h % 12 === 0 ? 12 : h % 12, minute: mi, pm: h >= 12 };
    }
    function label12(t) { return t.hour + ":" + pad(t.minute) + " " + (t.pm ? "PM" : "AM"); }

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
        // Date / time fields: js/thryd-ui.js sets up every one (data-datepicker / data-timepicker are no longer needed).
        $$("[data-task-files]", root).forEach(initFiles);
        $$("form[data-task-form]", root).forEach(initSummary);
        if (root.matches && root.matches("form[data-task-form]")) initSummary(root);
    }

    doc.addEventListener("app:enhance", function (e) { init(e.detail || doc); });
})();
