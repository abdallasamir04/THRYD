/* PTC administration helpers (progressive enhancement: every form works without them). */
(function () {
    "use strict";

    // Booking link: Copy Link, and keep the WhatsApp share link in step with the edited message.
    // Every share block on the page (the booking link, the feedback link and each booking's own feedback link).
    Array.prototype.forEach.call(document.querySelectorAll("[data-ptc-share]"), function (share) {
        var url = share.querySelector("[data-share-url]");
        var copy = share.querySelector("[data-copy-link]");
        var message = share.querySelector("[data-share-message]");
        var whatsapp = share.querySelector("[data-share-whatsapp]");
        if (copy && url) {
            copy.addEventListener("click", function () {
                var label = copy.innerHTML;
                var done = function () { copy.textContent = "Copied"; setTimeout(function () { copy.innerHTML = label; }, 2000); };
                if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(url.value).then(done, function () { url.select(); });
                else { url.select(); try { document.execCommand("copy"); done(); } catch (e) { /* the link stays selected */ } }
            });
        }
        if (message && whatsapp) {
            message.addEventListener("input", function () { whatsapp.href = "https://wa.me/?text=" + encodeURIComponent(message.value); });
        }
    });

    // Classes & dates: dim rows that do not take part, and "Apply to selected classes".
    var form = document.querySelector("[data-ptc-classes]");
    if (!form) return;

    function sync(row) {
        var on = row.querySelector("[data-include]");
        row.classList.toggle("is-off", !(on && on.checked));
    }

    form.querySelectorAll("[data-ptc-row]").forEach(function (row) {
        var on = row.querySelector("[data-include]");
        if (on) on.addEventListener("change", function () { sync(row); });
    });

    var apply = form.querySelector("[data-bulk-apply]");
    if (apply) {
        apply.addEventListener("click", function () {
            var values = {};
            form.querySelectorAll("[data-bulk]").forEach(function (el) { values[el.getAttribute("data-bulk")] = el.value; });
            var changed = 0;
            form.querySelectorAll("[data-ptc-row]").forEach(function (row) {
                var on = row.querySelector("[data-include]");
                if (!on || !on.checked) return;
                ["date", "start", "end"].forEach(function (key) {
                    var field = row.querySelector('[data-field="' + key + '"]');
                    if (field && values[key]) { field.value = values[key]; changed++; }
                });
            });
            if (changed === 0 && window.App && typeof window.App.alert === "function") {
                window.App.alert({ title: "Nothing to apply", message: "Tick the classes and choose a date or times first.", tone: "info" });
            }
        });
    }
})();
