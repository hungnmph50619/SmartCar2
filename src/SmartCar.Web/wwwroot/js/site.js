document.addEventListener("DOMContentLoaded", () => {
    initializeVietnameseDateInputs();
    initializeForms();
    initializeImageInputs();
    initializeAdminCustomerNavigation();
});

function initializeForms() {
    document.querySelectorAll("form[data-confirm], form[data-loading-form]").forEach((form) => {
        form.addEventListener("submit", (event) => {
            if (event.defaultPrevented || !form.checkValidity()) {
                return;
            }

            const confirmationMessage = form.dataset.confirm;
            if (confirmationMessage && !window.confirm(confirmationMessage)) {
                event.preventDefault();
                return;
            }

            if (!form.hasAttribute("data-loading-form")) {
                return;
            }

            const submitter = event.submitter ?? form.querySelector('button[type="submit"], input[type="submit"]');
            if (!submitter || submitter.disabled) {
                return;
            }

            submitter.disabled = true;
            submitter.setAttribute("aria-busy", "true");

            if (submitter instanceof HTMLButtonElement) {
                const loadingText = submitter.dataset.loadingText ?? "Đang xử lý...";
                submitter.dataset.originalHtml = submitter.innerHTML;
                submitter.innerHTML = `
                    <span class="spinner-border spinner-border-sm me-2" aria-hidden="true"></span>
                    <span>${escapeHtml(loadingText)}</span>`;
            } else {
                submitter.dataset.originalValue = submitter.value;
                submitter.value = submitter.dataset.loadingText ?? "Đang xử lý...";
            }
        });
    });
}

function initializeVietnameseDateInputs() {
    document.querySelectorAll("input[data-vn-date-input]").forEach((displayInput) => {
        if (!(displayInput instanceof HTMLInputElement)) {
            return;
        }

        const hiddenSelector = displayInput.dataset.hiddenTarget;
        const hiddenInput = hiddenSelector ? document.querySelector(hiddenSelector) : null;
        if (!(hiddenInput instanceof HTMLInputElement)) {
            return;
        }

        const syncToHidden = () => {
            const value = displayInput.value.trim();
            if (!value) {
                hiddenInput.value = "";
                displayInput.setCustomValidity("");
                return true;
            }

            const match = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(value);
            if (!match) {
                hiddenInput.value = "";
                displayInput.setCustomValidity("Vui lòng nhập ngày theo định dạng dd/mm/yyyy.");
                return false;
            }

            const day = Number(match[1]);
            const month = Number(match[2]);
            const year = Number(match[3]);
            const date = new Date(year, month - 1, day);
            const isValid = date.getFullYear() === year &&
                date.getMonth() === month - 1 &&
                date.getDate() === day;

            if (!isValid) {
                hiddenInput.value = "";
                displayInput.setCustomValidity("Ngày không hợp lệ.");
                return false;
            }

            displayInput.setCustomValidity("");
            hiddenInput.value = `${year.toString().padStart(4, "0")}-${month.toString().padStart(2, "0")}-${day.toString().padStart(2, "0")}`;
            return true;
        };

        if (!displayInput.value && hiddenInput.value) {
            const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(hiddenInput.value);
            if (match) {
                displayInput.value = `${match[3]}/${match[2]}/${match[1]}`;
            }
        }

        displayInput.addEventListener("input", syncToHidden);
        displayInput.addEventListener("blur", () => {
            syncToHidden();
            if (!displayInput.checkValidity()) {
                displayInput.reportValidity();
            }
        });

        displayInput.form?.addEventListener("submit", (event) => {
            if (!syncToHidden()) {
                event.preventDefault();
                displayInput.reportValidity();
            }
        });
    });
}

function initializeImageInputs() {
    document.querySelectorAll("input[type='file'][data-image-input]").forEach((input) => {
        const previewSelector = input.dataset.previewTarget;
        const infoSelector = input.dataset.fileInfoTarget;
        const previewContainer = previewSelector ? document.querySelector(previewSelector) : null;
        const infoContainer = infoSelector ? document.querySelector(infoSelector) : null;
        const previewImage = previewContainer?.querySelector("img") ?? null;
        const previewList = previewContainer?.querySelector("[data-preview-list]") ?? null;
        const clearButton = previewContainer?.querySelector("[data-clear-image]") ?? null;
        let objectUrls = [];

        const releaseObjectUrls = () => {
            objectUrls.forEach((url) => URL.revokeObjectURL(url));
            objectUrls = [];
        };

        const clearPreview = () => {
            releaseObjectUrls();
            input.value = "";
            previewContainer?.classList.add("d-none");
            infoContainer?.classList.add("d-none");

            if (previewImage) {
                previewImage.removeAttribute("src");
            }

            if (previewList) {
                previewList.innerHTML = "";
            }

            if (infoContainer) {
                infoContainer.textContent = "";
            }
        };

        input.addEventListener("change", () => {
            const files = Array.from(input.files ?? []);
            if (files.length === 0) {
                clearPreview();
                return;
            }

            if (files.some((file) => !file.type.startsWith("image/"))) {
                clearPreview();
                return;
            }

            releaseObjectUrls();
            objectUrls = files.map((file) => URL.createObjectURL(file));

            if (previewList) {
                previewList.innerHTML = "";
                files.forEach((file, index) => {
                    const column = document.createElement("div");
                    column.className = "col-6 col-md-4";

                    const image = document.createElement("img");
                    image.src = objectUrls[index];
                    image.alt = `Xem trước ${file.name}`;
                    image.className = "img-fluid rounded border w-100";
                    image.style.height = "140px";
                    image.style.objectFit = "cover";

                    const caption = document.createElement("div");
                    caption.className = "small text-truncate mt-1";
                    caption.title = file.name;
                    caption.textContent = file.name;

                    column.append(image, caption);
                    previewList.appendChild(column);
                });
            } else if (previewImage) {
                previewImage.src = objectUrls[0];
            }

            if (infoContainer) {
                const totalSize = files.reduce((sum, file) => sum + file.size, 0);
                infoContainer.textContent = files.length === 1
                    ? `${files[0].name} · ${formatBytes(files[0].size)}`
                    : `${files.length} ảnh · Tổng dung lượng ${formatBytes(totalSize)}`;
                infoContainer.classList.remove("d-none");
            }

            previewContainer?.classList.remove("d-none");
        });

        clearButton?.addEventListener("click", clearPreview);
        window.addEventListener("beforeunload", releaseObjectUrls, { once: true });
    });
}

function initializeAdminCustomerNavigation() {
    const isCustomerAdminPage = window.location.pathname.toLowerCase().startsWith("/admincustomers");

    if (isCustomerAdminPage && !document.querySelector('link[href*="admin-customers.css"]')) {
        const stylesheet = document.createElement("link");
        stylesheet.rel = "stylesheet";
        stylesheet.href = "/css/admin-customers.css";
        document.head.appendChild(stylesheet);
    }

    const customerLink = Array.from(document.querySelectorAll(".admin-sidebar a.admin-nav-link"))
        .find((link) => link.getAttribute("href")?.includes("/AdminDocuments"));

    if (!customerLink) {
        return;
    }

    customerLink.setAttribute("href", "/AdminCustomers");
    const label = customerLink.querySelector("span:last-child");
    if (label) {
        label.textContent = "Quản lý khách hàng";
    }

    if (isCustomerAdminPage) {
        customerLink.classList.add("active");
    }
}

function formatBytes(bytes) {
    if (bytes < 1024) {
        return `${bytes} B`;
    }

    if (bytes < 1024 * 1024) {
        return `${(bytes / 1024).toFixed(1)} KB`;
    }

    return `${(bytes / 1024 / 1024).toFixed(2)} MB`;
}

function escapeHtml(value) {
    const element = document.createElement("div");
    element.textContent = value;
    return element.innerHTML;
}
