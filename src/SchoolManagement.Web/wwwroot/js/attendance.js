/*
 * Session Attendance register: find a student and "mark not recorded as present". Display helpers
 * only — the server validates and saves (app.js submits the form and guards unsaved changes).
 */
(function () {
    "use strict";

    var form = document.querySelector("[data-session-register]");
    if (!form) return;
    var rows = Array.prototype.slice.call(form.querySelectorAll(".att-row"));

    var search = form.querySelector("[data-register-search]");
    if (search) search.addEventListener("input", function () {
        var term = search.value.trim().toLowerCase();
        rows.forEach(function (r) { r.hidden = term !== "" && r.getAttribute("data-name").indexOf(term) < 0; });
    });

    var allPresent = form.querySelector("[data-register-all-present]");
    if (allPresent) allPresent.addEventListener("click", function () {
        var marked = 0;
        rows.forEach(function (r) {
            if (r.hidden || r.querySelector('input[type=radio]:checked')) return;
            var present = r.querySelector('input[type=radio][value="Present"]');
            if (present) { present.checked = true; marked++; }
        });
        if (window.App && App.toast) {
            App.toast(marked === 0 ? "Every shown student already has a status." : marked + " student(s) marked present — not saved yet.", marked === 0 ? "info" : "success");
        }
    });
})();
