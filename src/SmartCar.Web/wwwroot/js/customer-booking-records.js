document.addEventListener("DOMContentLoaded", () => {
    if (!/^\/Bookings\/Details(?:\/|$)/i.test(window.location.pathname)) {
        return;
    }

    initializeCustomerTripRecordCards();
});

function initializeCustomerTripRecordCards() {
    const recordTitles = new Set(["Biên bản giao xe", "Biên bản trả xe"]);

    document.querySelectorAll(".card").forEach((card) => {
        const header = card.querySelector(":scope > .card-header");
        const body = card.querySelector(":scope > .card-body");
        const title = header?.querySelector("strong")?.textContent?.trim();

        if (!header || !body || !title || !recordTitles.has(title)) {
            return;
        }

        const signedLinks = Array.from(body.querySelectorAll("a[href*='signed-handover-'], a[href*='signed-return-']"));
        const signedPageCount = signedLinks.length;

        signedLinks.forEach((link, index) => {
            link.className = "btn btn-sm btn-outline-success text-decoration-none";
            link.textContent = `Trang ${index + 1}`;
        });

        body.querySelectorAll("strong").forEach((strong) => {
            const text = strong.textContent?.trim() ?? "";
            if (text === "Bản ký biên bản giao:" || text === "Bản ký biên bản trả:" || text.startsWith("Bản ký ")) {
                strong.textContent = signedPageCount > 0
                    ? `Bản ký (${signedPageCount} trang):`
                    : "Bản ký:";
            }
        });

        card.classList.add("customer-record-card");
        body.classList.add("d-none", "customer-record-details");

        const heading = document.createElement("div");
        heading.className = "d-flex align-items-center gap-2 flex-wrap";

        const headingText = document.createElement("strong");
        headingText.textContent = title;

        const signedBadge = document.createElement("span");
        signedBadge.className = signedPageCount > 0
            ? "badge bg-success"
            : "badge bg-secondary";
        signedBadge.textContent = signedPageCount > 0
            ? "Đã ký"
            : "Chưa ký";

        heading.append(headingText, signedBadge);

        const actions = document.createElement("div");
        actions.className = "d-flex align-items-center gap-2 flex-wrap";

        const toggleButton = document.createElement("button");
        toggleButton.type = "button";
        toggleButton.className = "btn btn-sm btn-primary";
        toggleButton.textContent = "Xem chi tiết";
        toggleButton.setAttribute("aria-expanded", "false");

        toggleButton.addEventListener("click", () => {
            const isHidden = body.classList.toggle("d-none");
            toggleButton.textContent = isHidden ? "Xem chi tiết" : "Ẩn chi tiết";
            toggleButton.setAttribute("aria-expanded", isHidden ? "false" : "true");
        });

        actions.appendChild(toggleButton);

        header.innerHTML = "";
        header.className = "card-header bg-white d-flex justify-content-between align-items-center gap-2 flex-wrap py-3";
        header.append(heading, actions);

        body.querySelectorAll(".col-6.col-md-4").forEach((column) => {
            if (column.querySelector("img")) {
                column.classList.add("customer-record-photo");
                column.querySelectorAll(":scope > .small.fw-semibold.mb-1").forEach((label) => label.remove());
            }
        });
    });

    if (!document.getElementById("customer-record-card-styles")) {
        const style = document.createElement("style");
        style.id = "customer-record-card-styles";
        style.textContent = `
            .customer-record-card .customer-record-details {
                border-top: 1px solid #eef1f4;
            }

            .customer-record-card .customer-record-photo {
                flex: 0 0 128px;
                width: 128px;
                max-width: 128px;
            }

            .customer-record-card .customer-record-photo img {
                height: 82px !important;
                object-fit: cover;
            }
        `;
        document.head.appendChild(style);
    }
}
