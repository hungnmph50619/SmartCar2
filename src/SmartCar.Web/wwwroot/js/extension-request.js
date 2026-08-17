document.addEventListener("DOMContentLoaded", () => {
    document.querySelectorAll('form[action*="/Extensions/SubmitRequest"]').forEach((form) => {
        const forceMajeure = form.querySelector('input[name="isForceMajeure"]');
        const customerNote = form.querySelector('input[name="CustomerNote"], textarea[name="CustomerNote"]');
        const evidenceNote = form.querySelector('textarea[name="evidenceNote"]');

        if (!(forceMajeure instanceof HTMLInputElement) || !(evidenceNote instanceof HTMLTextAreaElement)) {
            return;
        }

        form.enctype = "multipart/form-data";

        let evidenceImage = form.querySelector('input[name="evidenceImage"]');
        let latitude = form.querySelector('input[name="evidenceLatitude"]');
        let longitude = form.querySelector('input[name="evidenceLongitude"]');
        let evidencePanel = form.querySelector('[data-extension-force-evidence]');

        if (!evidencePanel) {
            evidencePanel = document.createElement("div");
            evidencePanel.className = "col-12";
            evidencePanel.dataset.extensionForceEvidence = "true";
            evidencePanel.innerHTML = `
                <div class="border rounded p-3 bg-light">
                    <div class="fw-semibold mb-2">Minh chứng bắt buộc khi bất khả kháng</div>
                    <div class="row g-3">
                        <div class="col-md-6">
                            <label class="form-label">Ảnh tình trạng xe / sự cố</label>
                            <input type="file" name="evidenceImage" accept="image/jpeg,image/png,image/webp" class="form-control" />
                            <div class="form-text">JPG, PNG hoặc WEBP; tối đa 5 MB.</div>
                        </div>
                        <div class="col-md-6">
                            <label class="form-label">Vị trí trực tiếp</label>
                            <div class="d-flex gap-2 flex-wrap">
                                <button type="button" class="btn btn-outline-secondary" data-extension-live-location>Lấy vị trí hiện tại</button>
                                <span class="small text-muted align-self-center" data-extension-location-status>Chưa lấy vị trí.</span>
                            </div>
                            <input type="hidden" name="evidenceLatitude" />
                            <input type="hidden" name="evidenceLongitude" />
                            <div class="form-text">Trình duyệt sẽ hỏi quyền vị trí. SmartCar dùng tọa độ này để xác minh tình huống bất khả kháng.</div>
                        </div>
                    </div>
                </div>`;

            const submitColumn = form.querySelector('button[type="submit"]')?.closest(".col-md-3, .col-md-2, .col-12");
            if (submitColumn?.parentElement === form) {
                form.insertBefore(evidencePanel, submitColumn);
            } else {
                form.appendChild(evidencePanel);
            }

            evidenceImage = evidencePanel.querySelector('input[name="evidenceImage"]');
            latitude = evidencePanel.querySelector('input[name="evidenceLatitude"]');
            longitude = evidencePanel.querySelector('input[name="evidenceLongitude"]');
        }

        const locationButton = evidencePanel.querySelector("[data-extension-live-location]");
        const locationStatus = evidencePanel.querySelector("[data-extension-location-status]");

        const syncRequirements = () => {
            const required = forceMajeure.checked;
            evidencePanel.classList.toggle("d-none", !required);

            if (customerNote instanceof HTMLInputElement || customerNote instanceof HTMLTextAreaElement) {
                customerNote.required = required;
            }
            evidenceNote.required = required;
            if (evidenceImage instanceof HTMLInputElement) {
                evidenceImage.required = required;
            }

            if (!required) {
                if (latitude instanceof HTMLInputElement) latitude.setCustomValidity("");
                if (longitude instanceof HTMLInputElement) longitude.setCustomValidity("");
            }
        };

        locationButton?.addEventListener("click", () => {
            if (!navigator.geolocation) {
                if (locationStatus) locationStatus.textContent = "Thiết bị không hỗ trợ định vị.";
                return;
            }

            locationButton.disabled = true;
            if (locationStatus) locationStatus.textContent = "Đang lấy vị trí...";

            navigator.geolocation.getCurrentPosition(
                (position) => {
                    const lat = position.coords.latitude.toFixed(6);
                    const lng = position.coords.longitude.toFixed(6);
                    if (latitude instanceof HTMLInputElement) {
                        latitude.value = lat;
                        latitude.setCustomValidity("");
                    }
                    if (longitude instanceof HTMLInputElement) {
                        longitude.value = lng;
                        longitude.setCustomValidity("");
                    }

                    const locationText = `Vị trí trực tiếp: ${lat}, ${lng}`;
                    const currentText = evidenceNote.value
                        .replace(/\s*\|?\s*Vị trí trực tiếp:\s*-?\d+(?:\.\d+)?,\s*-?\d+(?:\.\d+)?/i, "")
                        .trim();
                    evidenceNote.value = currentText ? `${currentText} | ${locationText}` : locationText;
                    if (locationStatus) locationStatus.textContent = `Đã lấy vị trí ${lat}, ${lng}.`;
                    locationButton.disabled = false;
                },
                () => {
                    if (locationStatus) locationStatus.textContent = "Không lấy được vị trí. Hãy cấp quyền vị trí rồi thử lại.";
                    locationButton.disabled = false;
                },
                { enableHighAccuracy: true, timeout: 10000, maximumAge: 0 });
        });

        form.addEventListener("submit", (event) => {
            if (!forceMajeure.checked) {
                return;
            }

            const hasLocation = latitude instanceof HTMLInputElement && longitude instanceof HTMLInputElement &&
                latitude.value.trim() && longitude.value.trim();

            if (!hasLocation) {
                event.preventDefault();
                if (latitude instanceof HTMLInputElement) {
                    latitude.setCustomValidity("Vui lòng bấm Lấy vị trí hiện tại trước khi gửi yêu cầu bất khả kháng.");
                    latitude.reportValidity();
                }
                if (locationStatus) locationStatus.textContent = "Cần lấy vị trí trực tiếp trước khi gửi.";
            }
        });

        forceMajeure.addEventListener("change", syncRequirements);
        syncRequirements();
    });
});
