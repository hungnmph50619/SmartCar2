(() => {
    const jsonRequest = async (url, options = {}) => {
        const response = await fetch(url, {
            credentials: "same-origin",
            ...options
        });

        let payload = null;
        try {
            payload = await response.json();
        } catch {
            payload = null;
        }

        if (!response.ok) {
            const errors = Array.isArray(payload?.errors)
                ? payload.errors
                : [payload?.message || "Không thể xử lý yêu cầu eKYC."];
            const error = new Error(errors.join(" "));
            error.errors = errors;
            throw error;
        }

        return payload;
    };

    const formatBytes = bytes => {
        if (!Number.isFinite(bytes)) return "";
        if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
        return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
    };

    const toIsoDate = value => {
        if (!value) return "";
        const match = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(value.trim());
        if (!match) return "";
        return `${match[3]}-${match[2].padStart(2, "0")}-${match[1].padStart(2, "0")}`;
    };

    const setInputValue = (selector, value) => {
        if (!value) return;
        const input = document.querySelector(selector);
        if (!input) return;
        input.value = value;
        input.dispatchEvent(new Event("input", { bubbles: true }));
        input.dispatchEvent(new Event("change", { bubbles: true }));
    };

    const setDateValue = (displaySelector, hiddenSelector, value) => {
        if (!value) return;
        const display = document.querySelector(displaySelector);
        const hidden = document.querySelector(hiddenSelector);
        if (display) {
            display.value = value;
            display.dispatchEvent(new Event("input", { bubbles: true }));
            display.dispatchEvent(new Event("change", { bubbles: true }));
        }
        if (hidden) {
            hidden.value = toIsoDate(value);
            hidden.dispatchEvent(new Event("change", { bubbles: true }));
        }
    };

    const getToken = form => form.querySelector('input[name="__RequestVerificationToken"]')?.value || "";

    const addToken = (form, formData) => {
        const token = getToken(form);
        if (token) formData.append("__RequestVerificationToken", token);
    };

    const setPanelMessage = (panel, message, type = "info") => {
        const box = panel.querySelector("[data-ekyc-message]");
        if (!box) return;
        box.className = `alert alert-${type} py-2 mb-3`;
        box.textContent = message;
        box.classList.remove("d-none");
    };

    const clearPanelMessage = panel => {
        const box = panel.querySelector("[data-ekyc-message]");
        if (!box) return;
        box.classList.add("d-none");
        box.textContent = "";
    };

    const latestSummaryHtml = latest => {
        if (!latest) return "";
        const face = latest.faceSimilarity == null ? "N/A" : `${Number(latest.faceSimilarity).toFixed(1)}%`;
        const checkedAt = latest.checkedAtUtc ? new Date(latest.checkedAtUtc).toLocaleString("vi-VN") : "";
        return `
            <div class="border rounded-3 bg-light p-3 mt-3" data-ekyc-latest>
                <div class="fw-semibold mb-2">Kết quả eKYC gần nhất</div>
                <div class="row g-2 small">
                    <div class="col-sm-6">OCR giấy tờ: <strong>${latest.ocrSucceeded ? "Đạt" : "Chưa đạt"}</strong></div>
                    <div class="col-sm-6">Người thật: <strong>${latest.livenessPassed === true ? "Đạt" : latest.livenessPassed === false ? "Không đạt" : "Chưa kiểm tra"}</strong></div>
                    <div class="col-sm-6">Khớp khuôn mặt: <strong>${latest.faceMatched === true ? "Đạt" : latest.faceMatched === false ? "Không đạt" : "Chưa kiểm tra"}</strong></div>
                    <div class="col-sm-6">Độ tương đồng: <strong>${face}</strong></div>
                </div>
                <div class="small text-muted mt-2">${latest.provider || "eKYC"}${latest.isDemo ? " · Chế độ demo" : ""}${checkedAt ? ` · ${checkedAt}` : ""}</div>
                <div class="small mt-1">${latest.message || ""}</div>
            </div>`;
    };

    const createCitizenPanel = () => {
        const panel = document.createElement("div");
        panel.className = "border border-primary-subtle rounded-4 bg-primary-subtle bg-opacity-10 p-3 p-md-4 mb-4";
        panel.dataset.ekycPanel = "citizen";
        panel.innerHTML = `
            <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap mb-3">
                <div>
                    <div class="text-uppercase small text-primary fw-bold">eKYC tự động</div>
                    <h4 class="h5 fw-bold mb-1">Đọc CCCD + kiểm tra khuôn mặt</h4>
                    <p class="text-muted small mb-0">Chọn hai ảnh CCCD, hệ thống đọc thông tin; sau đó quay video khuôn mặt 3–5 giây để kiểm tra người thật và so khớp với ảnh trên CCCD.</p>
                </div>
                <span class="badge bg-secondary" data-ekyc-provider>Đang kiểm tra cấu hình...</span>
            </div>

            <div class="alert alert-info py-2 mb-3 d-none" data-ekyc-message></div>

            <div class="row g-3 mb-3">
                <div class="col-md-4">
                    <div class="border rounded-3 bg-white p-3 h-100">
                        <div class="small text-muted">1. Ảnh CCCD</div>
                        <div class="fw-semibold mb-2">Chọn đủ hai mặt</div>
                        <button type="button" class="btn btn-sm btn-outline-primary me-1 mb-1" data-ekyc-pick="front">Chọn mặt trước</button>
                        <button type="button" class="btn btn-sm btn-outline-primary mb-1" data-ekyc-pick="back">Chọn mặt sau</button>
                        <div class="small text-muted mt-2" data-ekyc-file-state>Chưa chọn đủ ảnh.</div>
                    </div>
                </div>
                <div class="col-md-4">
                    <div class="border rounded-3 bg-white p-3 h-100">
                        <div class="small text-muted">2. OCR giấy tờ</div>
                        <div class="fw-semibold mb-2">Tự động điền thông tin</div>
                        <button type="button" class="btn btn-sm btn-primary" data-ekyc-ocr>Đọc CCCD tự động</button>
                        <div class="small text-muted mt-2" data-ekyc-ocr-state>Chưa đọc CCCD.</div>
                    </div>
                </div>
                <div class="col-md-4">
                    <div class="border rounded-3 bg-white p-3 h-100">
                        <div class="small text-muted">3. Khuôn mặt</div>
                        <div class="fw-semibold mb-2">Liveness + Face Match</div>
                        <button type="button" class="btn btn-sm btn-outline-primary me-1 mb-1" data-ekyc-camera-start>Bật camera</button>
                        <button type="button" class="btn btn-sm btn-primary mb-1" data-ekyc-record disabled>Quay 5 giây</button>
                        <button type="button" class="btn btn-sm btn-outline-secondary mb-1" data-ekyc-video-upload>Chọn video có sẵn</button>
                        <div class="small text-muted mt-2" data-ekyc-video-state>Chưa có video khuôn mặt.</div>
                    </div>
                </div>
            </div>

            <div class="ratio ratio-16x9 rounded-3 overflow-hidden bg-dark d-none mb-3" data-ekyc-camera-wrap style="max-width:520px">
                <video muted playsinline autoplay data-ekyc-camera></video>
            </div>

            <input type="hidden" name="CitizenIdVerification.EkycSessionId" data-ekyc-session />
            <input type="file" name="CitizenIdVerification.SelfieVideo" accept="video/webm,video/mp4,video/quicktime,video/*" capture="user" class="visually-hidden" data-ekyc-video-input />

            <div class="d-flex align-items-center justify-content-between gap-2 flex-wrap">
                <div class="small text-muted">Kết quả eKYC chỉ hỗ trợ Quản trị viên duyệt hồ sơ; không được hiển thị như xác nhận của C06 nếu chưa có dịch vụ đối soát chính thức.</div>
                <button type="button" class="btn btn-sm btn-link text-decoration-none" data-ekyc-manual>Dùng xác minh thủ công</button>
            </div>
            <div data-ekyc-latest-host></div>`;
        return panel;
    };

    const createLicensePanel = () => {
        const panel = document.createElement("div");
        panel.className = "border rounded-4 bg-light p-3 mb-4";
        panel.dataset.ekycPanel = "license";
        panel.innerHTML = `
            <div class="d-flex justify-content-between align-items-center gap-3 flex-wrap">
                <div>
                    <div class="small text-uppercase text-primary fw-bold">OCR GPLX</div>
                    <div class="fw-semibold">Đọc thông tin GPLX tự động</div>
                    <div class="small text-muted">Chọn đủ mặt trước và mặt sau rồi bấm đọc để tự điền họ tên, số GPLX, hạng và ngày.</div>
                </div>
                <div class="d-flex gap-2 flex-wrap">
                    <button type="button" class="btn btn-sm btn-outline-primary" data-license-pick="front">Chọn mặt trước</button>
                    <button type="button" class="btn btn-sm btn-outline-primary" data-license-pick="back">Chọn mặt sau</button>
                    <button type="button" class="btn btn-sm btn-primary" data-license-ocr>Đọc GPLX</button>
                </div>
            </div>
            <div class="alert alert-info py-2 mt-3 mb-0 d-none" data-license-message></div>`;
        return panel;
    };

    const setupCitizenEkyc = async () => {
        const form = document.querySelector('form[action*="SubmitCitizenId"]');
        if (!form || form.querySelector("[data-ekyc-panel='citizen']")) return;

        const fieldset = form.querySelector("fieldset");
        const panel = createCitizenPanel();
        form.insertBefore(panel, fieldset || form.firstChild);

        const frontInput = document.getElementById("citizen-front-file");
        const backInput = document.getElementById("citizen-back-file");
        const sessionInput = panel.querySelector("[data-ekyc-session]");
        const selfieInput = panel.querySelector("[data-ekyc-video-input]");
        const fileState = panel.querySelector("[data-ekyc-file-state]");
        const ocrState = panel.querySelector("[data-ekyc-ocr-state]");
        const videoState = panel.querySelector("[data-ekyc-video-state]");
        const cameraWrap = panel.querySelector("[data-ekyc-camera-wrap]");
        const video = panel.querySelector("[data-ekyc-camera]");
        const recordButton = panel.querySelector("[data-ekyc-record]");
        const submitButton = form.querySelector('button[type="submit"]');
        let stream = null;

        try {
            const status = await jsonRequest("/Ekyc/Status");
            const badge = panel.querySelector("[data-ekyc-provider]");
            if (badge) {
                badge.textContent = status.isDemo ? "Demo eKYC" : (status.provider || "eKYC");
                badge.className = `badge ${status.isDemo ? "bg-warning text-dark" : "bg-success"}`;
            }
            const host = panel.querySelector("[data-ekyc-latest-host]");
            if (host && status.latest) host.innerHTML = latestSummaryHtml(status.latest);
            if (status.isDemo) {
                setPanelMessage(panel, "SmartCar đang ở chế độ demo vì chưa có API key eKYC. Luồng camera hoạt động nhưng kết quả AI là mô phỏng và vẫn bắt buộc Admin duyệt.", "warning");
            }
        } catch {
            // Không chặn luồng xác minh thủ công nếu endpoint trạng thái tạm thời lỗi.
        }

        const updateFileState = () => {
            if (!fileState) return;
            const front = frontInput?.files?.[0];
            const back = backInput?.files?.[0];
            if (front && back) {
                fileState.textContent = `Đã chọn 2 ảnh · ${formatBytes(front.size + back.size)}`;
                fileState.className = "small text-success mt-2";
            } else if (front || back) {
                fileState.textContent = "Đã chọn 1/2 ảnh.";
                fileState.className = "small text-warning mt-2";
            } else {
                fileState.textContent = "Chưa chọn đủ ảnh.";
                fileState.className = "small text-muted mt-2";
            }
        };

        frontInput?.addEventListener("change", updateFileState);
        backInput?.addEventListener("change", updateFileState);
        panel.querySelector('[data-ekyc-pick="front"]')?.addEventListener("click", () => frontInput?.click());
        panel.querySelector('[data-ekyc-pick="back"]')?.addEventListener("click", () => backInput?.click());

        panel.querySelector("[data-ekyc-ocr]")?.addEventListener("click", async event => {
            clearPanelMessage(panel);
            const button = event.currentTarget;
            const front = frontInput?.files?.[0];
            const back = backInput?.files?.[0];
            if (!front || !back) {
                setPanelMessage(panel, "Vui lòng chọn đủ ảnh CCCD mặt trước và mặt sau trước khi đọc OCR.", "warning");
                return;
            }

            button.disabled = true;
            const oldText = button.textContent;
            button.textContent = "Đang đọc CCCD...";
            try {
                const data = new FormData();
                addToken(form, data);
                data.append("frontImage", front);
                data.append("backImage", back);
                const result = await jsonRequest("/Ekyc/PreviewCitizenId", { method: "POST", body: data });

                sessionInput.value = result.sessionId || "";
                setInputValue('[name="CitizenIdVerification.FullNameOnDocument"]', result.fullName);
                setInputValue('[name="CitizenIdVerification.DocumentNumber"]', result.documentNumber);
                setInputValue('[name="CitizenIdVerification.Gender"]', result.gender);
                setInputValue('[name="CitizenIdVerification.PermanentAddress"]', result.address);
                setDateValue("#citizen-birth-display", "#citizen-birth-value", result.dateOfBirth);
                setDateValue("#citizen-issued-display", "#citizen-issued-value", result.issuedDate);
                setDateValue("#citizen-expiry-display", "#citizen-expiry-value", result.expiryDate);

                if (ocrState) {
                    ocrState.textContent = result.isDemo
                        ? "Đã tạo phiên demo; thông tin vẫn cần nhập thủ công."
                        : `OCR thành công${result.ocrConfidence != null ? ` · ${Number(result.ocrConfidence).toFixed(1)}%` : ""}.`;
                    ocrState.className = `small ${result.isDemo ? "text-warning" : "text-success"} mt-2`;
                }
                setPanelMessage(panel, result.message || "Đã đọc CCCD. Hãy kiểm tra thông tin và quay khuôn mặt.", result.isDemo ? "warning" : "success");
            } catch (error) {
                sessionInput.value = "";
                setPanelMessage(panel, error.message, "danger");
                if (ocrState) {
                    ocrState.textContent = "OCR chưa thành công.";
                    ocrState.className = "small text-danger mt-2";
                }
            } finally {
                button.disabled = false;
                button.textContent = oldText;
            }
        });

        const stopCamera = () => {
            if (stream) {
                stream.getTracks().forEach(track => track.stop());
                stream = null;
            }
            if (video) video.srcObject = null;
        };

        panel.querySelector("[data-ekyc-camera-start]")?.addEventListener("click", async () => {
            clearPanelMessage(panel);
            if (!navigator.mediaDevices?.getUserMedia) {
                setPanelMessage(panel, "Trình duyệt không hỗ trợ camera trực tiếp. Hãy dùng nút Chọn video có sẵn.", "warning");
                return;
            }
            try {
                stopCamera();
                stream = await navigator.mediaDevices.getUserMedia({
                    video: { facingMode: "user", width: { ideal: 720 }, height: { ideal: 720 } },
                    audio: false
                });
                video.srcObject = stream;
                cameraWrap?.classList.remove("d-none");
                recordButton.disabled = false;
                setPanelMessage(panel, "Camera đã bật. Giữ khuôn mặt trong khung, đủ sáng và bấm Quay 5 giây.", "info");
            } catch {
                setPanelMessage(panel, "Không truy cập được camera. Hãy cấp quyền camera hoặc chọn video selfie có sẵn.", "danger");
            }
        });

        recordButton?.addEventListener("click", async () => {
            if (!stream || typeof MediaRecorder === "undefined") {
                setPanelMessage(panel, "Không thể quay trực tiếp trên trình duyệt này. Hãy chọn video selfie có sẵn.", "warning");
                return;
            }

            recordButton.disabled = true;
            clearPanelMessage(panel);
            const chunks = [];
            let recorder;
            try {
                const preferred = ["video/webm;codecs=vp8", "video/webm", "video/mp4"]
                    .find(type => MediaRecorder.isTypeSupported?.(type));
                recorder = preferred
                    ? new MediaRecorder(stream, { mimeType: preferred })
                    : new MediaRecorder(stream);
            } catch {
                setPanelMessage(panel, "Trình duyệt không hỗ trợ định dạng quay video. Hãy chọn video có sẵn.", "warning");
                recordButton.disabled = false;
                return;
            }

            recorder.addEventListener("dataavailable", event => {
                if (event.data?.size) chunks.push(event.data);
            });

            const stopped = new Promise(resolve => recorder.addEventListener("stop", resolve, { once: true }));
            recorder.start();
            if (videoState) {
                videoState.textContent = "Đang quay... giữ khuôn mặt trong khung.";
                videoState.className = "small text-danger mt-2";
            }
            await new Promise(resolve => setTimeout(resolve, 5000));
            recorder.stop();
            await stopped;

            const mime = recorder.mimeType || "video/webm";
            const extension = mime.includes("mp4") ? "mp4" : "webm";
            const blob = new Blob(chunks, { type: mime });
            const file = new File([blob], `selfie-${Date.now()}.${extension}`, { type: mime });

            try {
                const transfer = new DataTransfer();
                transfer.items.add(file);
                selfieInput.files = transfer.files;
                selfieInput.dispatchEvent(new Event("change", { bubbles: true }));
            } catch {
                setPanelMessage(panel, "Video đã quay nhưng trình duyệt không cho gắn file tự động. Hãy dùng nút Chọn video có sẵn.", "warning");
            }

            if (selfieInput.files?.length && videoState) {
                videoState.textContent = `Video 5 giây đã sẵn sàng · ${formatBytes(selfieInput.files[0].size)}`;
                videoState.className = "small text-success mt-2";
            }

            stopCamera();
            cameraWrap?.classList.add("d-none");
            recordButton.disabled = true;
        });

        panel.querySelector("[data-ekyc-video-upload]")?.addEventListener("click", () => selfieInput?.click());
        selfieInput?.addEventListener("change", () => {
            const file = selfieInput.files?.[0];
            if (file && videoState) {
                videoState.textContent = `Đã chọn ${file.name} · ${formatBytes(file.size)}`;
                videoState.className = "small text-success mt-2";
            }
        });

        panel.querySelector("[data-ekyc-manual]")?.addEventListener("click", () => {
            sessionInput.value = "";
            selfieInput.value = "";
            stopCamera();
            cameraWrap?.classList.add("d-none");
            setPanelMessage(panel, "Đã chuyển sang xác minh thủ công. Form sẽ được gửi theo luồng cũ và Quản trị viên đối chiếu trực tiếp.", "secondary");
        });

        form.addEventListener("submit", async event => {
            if (!sessionInput.value) return;

            event.preventDefault();
            event.stopImmediatePropagation();

            if (!selfieInput.files?.length) {
                setPanelMessage(panel, "Bạn đã dùng OCR eKYC nên cần quay/chọn video khuôn mặt trước khi gửi. Hoặc bấm Dùng xác minh thủ công.", "warning");
                return;
            }

            if (!form.checkValidity()) {
                form.reportValidity();
                setPanelMessage(panel, "Vui lòng kiểm tra lại các trường bắt buộc trước khi gửi eKYC.", "warning");
                return;
            }

            submitButton?.setAttribute("disabled", "disabled");
            const originalText = submitButton?.textContent;
            if (submitButton) submitButton.textContent = "Đang xác minh eKYC...";
            clearPanelMessage(panel);

            try {
                const result = await jsonRequest("/Ekyc/VerifyAndSubmitCitizenId", {
                    method: "POST",
                    body: new FormData(form),
                    headers: { "X-Requested-With": "XMLHttpRequest" }
                });
                setPanelMessage(panel, result.message || "eKYC thành công.", "success");
                window.location.assign(result.redirectUrl || "/Profile?tab=documents");
            } catch (error) {
                setPanelMessage(panel, error.message, "danger");
                if (submitButton) {
                    submitButton.removeAttribute("disabled");
                    submitButton.textContent = originalText || "Gửi xác minh CCCD";
                }
            }
        }, true);
    };

    const setupLicenseOcr = () => {
        const form = document.querySelector('form[action*="SubmitDrivingLicense"]');
        if (!form || form.querySelector("[data-ekyc-panel='license']")) return;
        const fieldset = form.querySelector("fieldset");
        const panel = createLicensePanel();
        form.insertBefore(panel, fieldset || form.firstChild);

        const frontInput = document.getElementById("license-front-file");
        const backInput = document.getElementById("license-back-file");
        const message = panel.querySelector("[data-license-message]");

        panel.querySelector('[data-license-pick="front"]')?.addEventListener("click", () => frontInput?.click());
        panel.querySelector('[data-license-pick="back"]')?.addEventListener("click", () => backInput?.click());
        panel.querySelector("[data-license-ocr]")?.addEventListener("click", async event => {
            const button = event.currentTarget;
            const front = frontInput?.files?.[0];
            const back = backInput?.files?.[0];
            if (!front || !back) {
                message.textContent = "Vui lòng chọn đủ ảnh GPLX mặt trước và mặt sau.";
                message.className = "alert alert-warning py-2 mt-3 mb-0";
                return;
            }

            button.disabled = true;
            const oldText = button.textContent;
            button.textContent = "Đang đọc...";
            try {
                const data = new FormData();
                addToken(form, data);
                data.append("frontImage", front);
                data.append("backImage", back);
                const result = await jsonRequest("/Ekyc/PreviewDrivingLicense", { method: "POST", body: data });

                setInputValue('[name="DrivingLicenseVerification.FullNameOnDocument"]', result.fullName);
                setInputValue('[name="DrivingLicenseVerification.DocumentNumber"]', result.documentNumber);
                setInputValue('[name="DrivingLicenseVerification.LicenseClass"]', result.licenseClass?.toUpperCase());
                setDateValue("#license-issued-display", "#license-issued-value", result.issuedDate);
                setDateValue("#license-expiry-display", "#license-expiry-value", result.expiryDate);

                message.textContent = result.message || "Đã đọc GPLX. Vui lòng kiểm tra lại thông tin.";
                message.className = `alert alert-${result.isDemo ? "warning" : "success"} py-2 mt-3 mb-0`;
            } catch (error) {
                message.textContent = error.message;
                message.className = "alert alert-danger py-2 mt-3 mb-0";
            } finally {
                button.disabled = false;
                button.textContent = oldText;
            }
        });
    };

    const setupAdminSummary = async () => {
        if (!/\/AdminCustomers\/Details/i.test(window.location.pathname)) return;
        const customerId = document.querySelector('input[name="customerId"]')?.value;
        if (!customerId) return;

        try {
            const payload = await jsonRequest(`/Ekyc/AdminSummary?customerId=${encodeURIComponent(customerId)}`);
            const result = payload?.result;
            if (!result) return;

            const sections = [...document.querySelectorAll("section.card")];
            const citizenSection = sections.find(section => section.textContent?.includes("Xác minh danh tính — CCCD"));
            if (!citizenSection || document.querySelector("[data-admin-ekyc-summary]")) return;

            const card = document.createElement("section");
            card.className = `card border-0 shadow-sm mb-4 ${result.isDemo ? "border-warning" : "border-primary"}`;
            card.dataset.adminEkycSummary = "";
            card.innerHTML = `
                <div class="card-body p-4">
                    <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap mb-3">
                        <div>
                            <div class="small text-uppercase text-primary fw-bold">Kết quả eKYC hỗ trợ duyệt</div>
                            <h3 class="h5 fw-bold mb-1">OCR + Liveness + Face Match</h3>
                            <div class="small text-muted">Nhà cung cấp: ${result.provider}${result.isDemo ? " · DEMO" : ""}</div>
                        </div>
                        <span class="badge ${result.livenessPassed === true && result.faceMatched === true && !result.isDemo ? "bg-success" : "bg-warning text-dark"}">${result.isDemo ? "Kết quả mô phỏng" : result.livenessPassed === true && result.faceMatched === true ? "AI đạt" : "Cần kiểm tra"}</span>
                    </div>
                    <div class="row g-3">
                        <div class="col-md-3"><div class="border rounded p-3 h-100"><span class="small text-muted d-block">OCR</span><strong>${result.ocrSucceeded ? "Đạt" : "Không đạt"}</strong></div></div>
                        <div class="col-md-3"><div class="border rounded p-3 h-100"><span class="small text-muted d-block">Người thật</span><strong>${result.livenessPassed === true ? "Đạt" : result.livenessPassed === false ? "Không đạt" : "N/A"}</strong></div></div>
                        <div class="col-md-3"><div class="border rounded p-3 h-100"><span class="small text-muted d-block">Khớp khuôn mặt</span><strong>${result.faceMatched === true ? "Đạt" : result.faceMatched === false ? "Không đạt" : "N/A"}</strong></div></div>
                        <div class="col-md-3"><div class="border rounded p-3 h-100"><span class="small text-muted d-block">Similarity</span><strong>${result.faceSimilarity == null ? "N/A" : `${Number(result.faceSimilarity).toFixed(1)}%`}</strong></div></div>
                    </div>
                    <div class="alert alert-light border small mt-3 mb-0"><strong>Lưu ý:</strong> kết quả AI chỉ là dữ liệu hỗ trợ. SmartCar vẫn yêu cầu Quản trị viên đối chiếu ảnh và thông tin trước khi bấm xác minh; không coi đây là xác nhận C06 nếu chưa tích hợp đối soát chính thức.</div>
                </div>`;
            citizenSection.insertAdjacentElement("beforebegin", card);
        } catch {
            // Admin vẫn có thể duyệt thủ công nếu không tải được kết quả eKYC.
        }
    };

    document.addEventListener("DOMContentLoaded", () => {
        setupCitizenEkyc();
        setupLicenseOcr();
        setupAdminSummary();
    });
})();
