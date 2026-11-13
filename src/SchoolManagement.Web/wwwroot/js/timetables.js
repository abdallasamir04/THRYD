/*
 * Timetables Center: select all / none, the selected count, and a THRYD toast while a ZIP is prepared
 * (the server builds the PDFs, then the browser downloads the file; the page stays where it is).
 */
(function () {
    "use strict";

    var all = document.querySelector("[data-tt-all]");
    var items = Array.prototype.slice.call(document.querySelectorAll("[data-tt-item]"));
    var count = document.querySelector("[data-tt-count]");
    var list = document.querySelector(".tt-list");
    var weeks = list ? parseInt(list.getAttribute("data-weeks"), 10) || 1 : 1;
    var selectedButton = document.querySelector('button[form="tt-download"]');

    function update() {
        var n = items.filter(function (i) { return i.checked; }).length;
        var docs = n * weeks;
        if (count) count.textContent = n + " of " + items.length + " selected · " + docs + " PDF" + (docs === 1 ? "" : "s") +
            (weeks > 1 ? " × " + weeks + " weeks" : "") + (docs > 1 ? " in one ZIP" : "");
        if (all) { all.checked = n === items.length; all.indeterminate = n > 0 && n < items.length; }
        if (selectedButton) selectedButton.disabled = n === 0;
    }

    if (all) all.addEventListener("change", function () {
        items.forEach(function (i) { i.checked = all.checked; });
        update();
    });
    items.forEach(function (i) { i.addEventListener("change", update); });
    update();

    document.addEventListener("click", function (e) {
        var el = e.target.closest("[data-preparing]");
        if (el && window.App && App.toast) App.toast(el.getAttribute("data-preparing"), "info");
    });
})();
