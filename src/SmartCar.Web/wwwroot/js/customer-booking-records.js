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

        card.classList.add("customer-record-card");
        body.classList.add("d-none", "customer-record-details");

        const existingSignedLink = Array.from(header.querySelectorAll("a"))
            .find((link) => link.textContent?.trim() === "Xem bản ký");

        const heading = document.createElement("div");
        heading.className = "d-flex align-items-center gap-2 flex-wrap";

        const headingText = document.createElement("strong");
        headingText.textContent = title;

        const signedBadge = document.createElement("span");
        signedBadge.className = existingSignedLink
            ? "badge bg-success"
            : "badge bg-secondary";
        signedBadge.textContent = existingSignedLink
            ? "Đã có bản ký"
            : "Chưa có bản ký";

        heading.append(headingText, signedBadge);

        const actions = document.createElement("div");
        actions.className = "d-flex align-items-center gap-2 flex-wrap";

        if (existingSignedLink) {
            existingSignedLink.className = "btn btn-sm btn-outline-primary text-decoration-none";
            actions.appendChild(existingSignedLink);
        }

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

            .customer-record-card .customer-record-photo .small {
                white-space: nowrap;
                overflow: hidden;
                text-overflow: ellipsis;
            }
        `;
        document.head.appendChild(style);
    }
}
