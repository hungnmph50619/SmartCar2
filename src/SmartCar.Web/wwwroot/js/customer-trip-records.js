(() => {
    const initializeCustomerTripRecords = () => {
        const recordTitles = new Set(["Biên bản giao xe", "Biên bản trả xe"]);

        document.querySelectorAll(".card").forEach((card) => {
            const header = card.querySelector(":scope > .card-header");
            const title = header?.querySelector("strong")?.textContent?.trim();
            if (!header || !title || !recordTitles.has(title)) {
                return;
            }

            const body = card.querySelector(":scope > .card-body");
            if (!body || body.dataset.compactRecordReady === "true") {
                return;
            }

            body.dataset.compactRecordReady = "true";

            const firstRow = body.querySelector(":scope > .row");
            const summaryValues = firstRow
                ? Array.from(firstRow.querySelectorAll(".fw-semibold"))
                    .map((item) => item.textContent?.trim())
                    .filter(Boolean)
                : [];

            const signedLink = Array.from(header.querySelectorAll("a"))
                .find((link) => link.textContent?.trim() === "Xem bản ký");

            const compact = document.createElement("div");
            compact.className = "card-body py-3";

            const row = document.createElement("div");
            row.className = "d-flex justify-content-between align-items-center gap-3 flex-wrap";

            const info = document.createElement("div");
            info.className = "small";

            const summaryLine = document.createElement("div");
            summaryLine.className = "fw-semibold";
            summaryLine.textContent = summaryValues.length > 0
                ? summaryValues.join(" · ")
                : "Đã có biên bản điện tử";
            info.appendChild(summaryLine);

            if (title === "Biên bản trả xe") {
                const damageLabel = Array.from(body.querySelectorAll("strong"))
                    .find((item) => item.textContent?.trim().startsWith("Hư hỏng mới:"));
                const damageValue = damageLabel?.nextElementSibling?.textContent?.trim();
                if (damageValue) {
                    const damageLine = document.createElement("div");
                    damageLine.className = damageValue === "Không"
                        ? "text-success mt-1"
                        : "text-danger mt-1";
                    damageLine.textContent = `Hư hỏng mới: ${damageValue}`;
                    info.appendChild(damageLine);
                }
            }

            const actions = document.createElement("div");
            actions.className = "d-flex gap-2 align-items-center flex-wrap";

            if (signedLink) {
                const badge = document.createElement("span");
                badge.className = "badge bg-success";
                badge.textContent = "Đã có chữ ký";
                actions.appendChild(badge);
            }

            const toggle = document.createElement("button");
            toggle.type = "button";
            toggle.className = "btn btn-sm btn-outline-primary";
            toggle.textContent = "Xem chi tiết";
            toggle.setAttribute("aria-expanded", "false");

            body.classList.add("d-none");

            toggle.addEventListener("click", () => {
                const isOpening = body.classList.contains("d-none");
                body.classList.toggle("d-none", !isOpening);
                toggle.textContent = isOpening ? "Thu gọn" : "Xem chi tiết";
                toggle.setAttribute("aria-expanded", isOpening ? "true" : "false");
            });

            actions.appendChild(toggle);
            row.append(info, actions);
            compact.appendChild(row);
            body.before(compact);
        });
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initializeCustomerTripRecords);
    } else {
        initializeCustomerTripRecords();
    }
})();
