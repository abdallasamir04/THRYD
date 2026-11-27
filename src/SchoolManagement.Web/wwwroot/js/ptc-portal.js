/* Parent PTC portal: one submission per click (the server also refuses a second booking for the same child). */
(function () {
    "use strict";
    document.querySelectorAll("form[data-pp-once]").forEach(function (form) {
        form.addEventListener("submit", function (e) {
            if (form.hasAttribute("data-pp-book") && !form.querySelector('input[name="subjects"]:checked')) {
                e.preventDefault();
                alert("Please choose at least one subject to discuss.");
                return;
            }
            if (form.dataset.sent) { e.preventDefault(); return; }
            form.dataset.sent = "1";
            form.querySelectorAll('button[type="submit"]').forEach(function (b) { b.disabled = true; });
        });
    });
})();
