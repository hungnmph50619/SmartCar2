// JavaScript dùng chung cho giao diện SmartCar.

document.addEventListener("DOMContentLoaded", () => {
    const home = document.querySelector(".sc-home");
    if (!home) {
        return;
    }

    home.querySelectorAll(".sc-link").forEach(link => {
        if (link.textContent?.toLowerCase().includes("tất cả xe")) {
            link.setAttribute("href", "/Vehicles");
        }
    });

    home.querySelectorAll(".sc-car-card__button").forEach((link, index) => {
        link.setAttribute("href", `/Vehicles/${index + 1}`);
    });

    home.querySelectorAll("a.btn[href='#featuredCars']").forEach(link => {
        link.setAttribute("href", "/Vehicles");
    });
});
