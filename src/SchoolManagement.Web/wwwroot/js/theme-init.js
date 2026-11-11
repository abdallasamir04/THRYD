// Runs in <head> before first paint so the chosen theme never flashes. Preference: light | dark | system.
(function () {
    var pref = "light";
    try { pref = localStorage.getItem("sm.theme") || "light"; } catch (e) { /* storage blocked */ }
    var dark = pref === "dark" || (pref === "system" && window.matchMedia && matchMedia("(prefers-color-scheme: dark)").matches);
    document.documentElement.setAttribute("data-theme", dark ? "dark" : "light");
    document.documentElement.setAttribute("data-theme-pref", pref);
})();
