(() => {
    const ensureStylesheet = () => {
        if (document.querySelector('link[data-ekyc-styles]')) return;
        const link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = '/css/ekyc.css';
        link.dataset.ekycStyles = '';
        document.head.appendChild(link);
    };

    const jsonRequest = async (url, options = {}) => {
        const response = await fetch(url, { credentials: 'same-origin', ...options });
        let payload = null;
        try { payload = await response.json(); } catch { payload = null; }

        if (!response.ok) {
            const errors = Array.isArray(payload?.errors)
                ? payload.errors
                : [payload?.message || 'Không thể xử lý yêu cầu xác minh giấy tờ điện tử.'];
            const error = new Error(errors.join(' '));
            error.errors = errors;
            throw error;
        }
        return payload;
    };

    const formatBytes = bytes => {
        if (!Number.isFinite(bytes)) return '';
        if (bytes < 1024 * 1024) return `${Math.max(1, Math.round(bytes / 1024))} KB`;
        return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
    };

    const toIsoDate = value => {
        if (!value) return '';
        const match = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(value.trim());
        if (!match) return '';
        return `${match[3]}-${match[2].padStart(2, '0')}-${match[1].padStart(2, '0')}`;
    };

    const setInputValue = (selector, value) => {
        if (!value) return;
        const input = document.querySelector(selector);
        if (!input) return;
        input.value = value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };

    const setDateValue = (displaySelector, hiddenSelector, value) => {
        if (!value) return;
        const display = document.querySelector(displaySelector);
        const hidden = document.querySelector(hiddenSelector);
        if (display) {
            display.value = value;
            display.dispatchEvent(new Event('input', { bubbles: true }));
            display.dispatchEvent(new Event('change', { bubbles: true }));
        }
        if (hidden) {
            hidden.value = toIsoDate(value);
            hidden.dispatchEvent(new Event('change', { bubbles: true }));
        }
    };

    const getToken = form => form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const addToken = (form, formData) => {
        const token = getToken(form);
        if (token) formData.append('__RequestVerificationToken', token);
    };

    const setMessage = (panel, message, type = 'info') => {
        const box = panel.querySelector('[data-ekyc-message]');
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = message;
        box.classList.remove('d-none');
    };

    const clearMessage = panel => {
        const box = panel.querySelector('[data-ekyc-message]');
        if (!box) return;
        box.textContent = '';
        box.classList.add('d-none');
    };

    const setLicenseMessage = (panel, message, type = 'info') => {
        const box = panel.querySelector('[data-license-message]');
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = message;
        box.classList.remove('d-none');
    };

    const validateContainer = container => {
        const controls = [...container.querySelectorAll('input, select, textarea')]
            .filter(control => !control.disabled && control.type !== 'hidden');
        for (const control of controls) {
            if (!control.checkValidity()) {
                control.reportValidity();
                control.focus({ preventScroll: true });
                control.scrollIntoView({ behavior: 'smooth', block: 'center' });
                return false;
            }
        }
        return true;
    };

    const latestSummaryHtml = latest => {
        if (!latest) return '';
        const face = latest.faceSimilarity == null ? 'N/A' : `${Number(latest.faceSimilarity).toFixed(1)}%`;
        const checkedAt = latest.checkedAtUtc ? new Date(latest.checkedAtUtc).toLocaleString('vi-VN') : '';
        return `
            <details class="ekyc-latest mt-3">
                <summary>Kết quả xác minh tự động gần nhất</summary>
                <div class="row g-2 small mt-2">
                    <div class="col-sm-6">OCR giấy tờ: <strong>${latest.ocrSucceeded ? 'Đạt' : 'Chưa đạt'}</strong></div>
                    <div class="col-sm-6">Người thật: <strong>${latest.livenessPassed === true ? 'Đạt' : latest.livenessPassed === false ? 'Không đạt' : 'Chưa kiểm tra'}</strong></div>
                    <div class="col-sm-6">Khớp khuôn mặt: <strong>${latest.faceMatched === true ? 'Đạt' : latest.faceMatched === false ? 'Không đạt' : 'Chưa kiểm tra'}</strong></div>
                    <div class="col-sm-6">Độ tương đồng: <strong>${face}</strong></div>
                </div>
                <div class="small text-muted mt-2">${latest.provider || 'Xác minh giấy tờ điện tử'}${latest.isDemo ? ' · Chế độ trình diễn' : ''}${checkedAt ? ` · ${checkedAt}` : ''}</div>
            </details>`;
    };

    const citizenPanelHtml = () => `
        <section class="ekyc-wizard" data-ekyc-panel="citizen">
            <div class="ekyc-wizard-header">
                <div>
                    <div class="ekyc-eyebrow">XÁC MINH TỰ ĐỘNG</div>
                    <h4>CCCD và khuôn mặt</h4>
                    <p>Hoàn thành 3 bước. Ảnh bạn chọn ở đây cũng chính là ảnh được gửi trong hồ sơ, không cần tải lại lần nữa.</p>
                </div>
                <span class="badge bg-secondary" data-ekyc-provider>Đang kiểm tra...</span>
            </div>

            <div class="d-none" data-ekyc-demo-notice></div>
            <div class="alert alert-info ekyc-message mb-3 d-none" data-ekyc-message></div>

            <div class="ekyc-stepper" aria-label="Tiến độ xác minh CCCD">
                <div class="ekyc-step is-active" data-ekyc-step-indicator="1"><span>1</span><strong>CCCD</strong></div>
                <div class="ekyc-step-line"></div>
                <div class="ekyc-step" data-ekyc-step-indicator="2"><span>2</span><strong>Thông tin</strong></div>
                <div class="ekyc-step-line"></div>
                <div class="ekyc-step" data-ekyc-step-indicator="3"><span>3</span><strong>Khuôn mặt</strong></div>
            </div>

            <div class="ekyc-pane" data-ekyc-step-pane="1">
                <div class="ekyc-pane-heading">
                    <span class="ekyc-pane-number">1</span>
                    <div><h5>Chụp hoặc tải CCCD</h5><p>Ảnh rõ nét, đủ 4 góc, không lóa và không che thông tin.</p></div>
                </div>
                <div class="row g-3 ekyc-upload-grid" data-ekyc-upload-grid></div>
                <div class="ekyc-actions">
                    <button type="button" class="btn btn-primary" data-ekyc-ocr disabled>Đọc CCCD và tiếp tục</button>
                    <button type="button" class="btn btn-link" data-ekyc-manual>Dùng xác minh thủ công</button>
                </div>
                <div class="small text-muted mt-2" data-ekyc-file-state>Chưa chọn đủ 2 ảnh CCCD.</div>
            </div>

            <div class="ekyc-pane d-none" data-ekyc-step-pane="2">
                <div class="ekyc-pane-heading">
                    <span class="ekyc-pane-number">2</span>
                    <div><h5>Kiểm tra thông tin</h5><p>Đối chiếu với CCCD. Nếu OCR đọc sai, bạn có thể sửa trực tiếp trước khi tiếp tục.</p></div>
                </div>
                <div class="small mb-3" data-ekyc-ocr-state></div>
                <div data-ekyc-form-fields></div>
                <div class="ekyc-actions justify-content-between">
                    <button type="button" class="btn btn-outline-secondary" data-ekyc-back="1">← Quay lại ảnh CCCD</button>
                    <button type="button" class="btn btn-primary" data-ekyc-next-face>Thông tin đúng, tiếp tục →</button>
                </div>
                <div class="d-none mt-3" data-ekyc-manual-submit></div>
            </div>

            <div class="ekyc-pane d-none" data-ekyc-step-pane="3">
                <div class="ekyc-pane-heading">
                    <span class="ekyc-pane-number">3</span>
                    <div><h5>Xác minh khuôn mặt</h5><p>Đưa khuôn mặt vào giữa khung hình và quay khoảng 5 giây.</p></div>
                </div>

                <div class="row g-4 align-items-start">
                    <div class="col-lg-6">
                        <div class="ekyc-camera-shell d-none" data-ekyc-camera-wrap>
                            <video muted playsinline autoplay data-ekyc-camera></video>
                            <div class="ekyc-face-guide" aria-hidden="true"></div>
                        </div>
                        <div class="ekyc-camera-placeholder" data-ekyc-camera-placeholder>
                            <div class="ekyc-camera-icon">◎</div>
                            <strong>Camera chưa bật</strong>
                            <span>Bấm “Bật camera” để bắt đầu.</span>
                        </div>
                    </div>
                    <div class="col-lg-6">
                        <div class="ekyc-face-tips">
                            <strong>Để nhận diện tốt hơn</strong>
                            <ul><li>Nhìn thẳng vào camera</li><li>Đảm bảo khuôn mặt đủ sáng</li><li>Không đeo khẩu trang hoặc kính tối màu</li></ul>
                        </div>
                        <div class="d-grid gap-2 mt-3">
                            <button type="button" class="btn btn-primary ekyc-camera-button" data-ekyc-camera-start>📷 Bật camera</button>
                            <button type="button" class="btn btn-danger" data-ekyc-record disabled>● Quay 5 giây</button>
                            <button type="button" class="btn btn-outline-secondary" data-ekyc-video-upload>Chọn video có sẵn</button>
                        </div>
                        <div class="small text-muted mt-2" data-ekyc-video-state>Chưa có video khuôn mặt.</div>
                    </div>
                </div>

                <input type="hidden" name="CitizenIdVerification.EkycSessionId" data-ekyc-session />
                <input type="file" name="CitizenIdVerification.SelfieVideo" accept="video/webm,video/mp4,video/quicktime,video/*" capture="user" class="visually-hidden" data-ekyc-video-input />

                <div class="ekyc-actions justify-content-between mt-4">
                    <button type="button" class="btn btn-outline-secondary" data-ekyc-back="2">← Quay lại thông tin</button>
                    <div data-ekyc-submit-host></div>
                </div>
                <p class="small text-muted mt-3 mb-0">Sau khi gửi, hồ sơ sẽ được SmartCar kiểm tra trước khi bạn có thể thuê xe.</p>
            </div>

            <div data-ekyc-latest-host></div>
        </section>`;

    const licensePanelHtml = () => `
        <section class="ekyc-wizard ekyc-license-wizard" data-ekyc-panel="license">
            <div class="ekyc-wizard-header">
                <div>
                    <div class="ekyc-eyebrow">GPLX</div>
                    <h4>Đọc giấy phép lái xe tự động</h4>
                    <p>Chọn hai mặt GPLX một lần, hệ thống sẽ hỗ trợ điền thông tin để bạn kiểm tra.</p>
                </div>
            </div>
            <div class="alert alert-info ekyc-message mb-3 d-none" data-license-message></div>
            <div class="ekyc-stepper ekyc-stepper-two">
                <div class="ekyc-step is-active" data-license-step-indicator="1"><span>1</span><strong>GPLX</strong></div>
                <div class="ekyc-step-line"></div>
                <div class="ekyc-step" data-license-step-indicator="2"><span>2</span><strong>Thông tin</strong></div>
            </div>

            <div class="ekyc-pane" data-license-step-pane="1">
                <div class="ekyc-pane-heading">
                    <span class="ekyc-pane-number">1</span>
                    <div><h5>Chụp hoặc tải GPLX</h5><p>Chụp trọn 4 góc, rõ chữ, không lóa. Ảnh này sẽ được dùng luôn trong hồ sơ.</p></div>
                </div>
                <div class="row g-3 ekyc-upload-grid" data-license-upload-grid></div>
                <div class="ekyc-actions">
                    <button type="button" class="btn btn-primary" data-license-ocr disabled>Đọc GPLX và tiếp tục</button>
                    <button type="button" class="btn btn-link" data-license-manual>Nhập thông tin thủ công</button>
                </div>
                <div class="small text-muted mt-2" data-license-file-state>Chưa chọn đủ 2 ảnh GPLX.</div>
            </div>

            <div class="ekyc-pane d-none" data-license-step-pane="2">
                <div class="ekyc-pane-heading">
                    <span class="ekyc-pane-number">2</span>
                    <div><h5>Kiểm tra thông tin GPLX</h5><p>Kiểm tra hạng bằng và thời hạn trước khi gửi xác minh.</p></div>
                </div>
                <div data-license-form-fields></div>
                <div class="ekyc-actions justify-content-between">
                    <button type="button" class="btn btn-outline-secondary" data-license-back>← Quay lại ảnh GPLX</button>
                    <div data-license-submit-host></div>
                </div>
            </div>
        </section>`;

    const decorateMovedUpload = (column, label) => {
        if (!column) return;
        column.classList.add('ekyc-upload-column');
        const box = document.createElement('div');
        box.className = 'ekyc-upload-card';
        while (column.firstChild) box.appendChild(column.firstChild);
        const title = document.createElement('div');
        title.className = 'ekyc-upload-card-title';
        title.textContent = label;
        box.prepend(title);
        column.appendChild(box);
    };

    const setupCitizenEkyc = async () => {
        const form = document.querySelector('form[action*="SubmitCitizenId"]');
        if (!form || form.querySelector('[data-ekyc-panel="citizen"]')) return;
        const fieldset = form.querySelector('fieldset');
        if (!fieldset || fieldset.disabled) return;

        const frontInput = document.getElementById('citizen-front-file');
        const backInput = document.getElementById('citizen-back-file');
        const submitButton = form.querySelector('button[type="submit"]');
        if (!frontInput || !backInput || !submitButton) return;

        const wrapper = document.createElement('div');
        wrapper.innerHTML = citizenPanelHtml().trim();
        const panel = wrapper.firstElementChild;
        form.insertBefore(panel, fieldset);

        const textRow = fieldset.querySelector('.row.g-3');
        const frontCol = frontInput.closest('.col-md-6');
        const backCol = backInput.closest('.col-md-6');
        const submitContainer = submitButton.closest('.d-flex') || submitButton.parentElement;

        const uploadGrid = panel.querySelector('[data-ekyc-upload-grid]');
        const fieldsHost = panel.querySelector('[data-ekyc-form-fields]');
        const submitHost = panel.querySelector('[data-ekyc-submit-host]');
        if (frontCol) { decorateMovedUpload(frontCol, 'Mặt trước CCCD'); uploadGrid.appendChild(frontCol); }
        if (backCol) { decorateMovedUpload(backCol, 'Mặt sau CCCD'); uploadGrid.appendChild(backCol); }
        if (textRow) fieldsHost.appendChild(textRow);
        if (submitContainer) submitHost.appendChild(submitContainer);
        fieldset.classList.add('d-none');

        const sessionInput = panel.querySelector('[data-ekyc-session]');
        const selfieInput = panel.querySelector('[data-ekyc-video-input]');
        const fileState = panel.querySelector('[data-ekyc-file-state]');
        const ocrState = panel.querySelector('[data-ekyc-ocr-state]');
        const videoState = panel.querySelector('[data-ekyc-video-state]');
        const cameraWrap = panel.querySelector('[data-ekyc-camera-wrap]');
        const cameraPlaceholder = panel.querySelector('[data-ekyc-camera-placeholder]');
        const video = panel.querySelector('[data-ekyc-camera]');
        const recordButton = panel.querySelector('[data-ekyc-record]');
        const ocrButton = panel.querySelector('[data-ekyc-ocr]');
        const nextFaceButton = panel.querySelector('[data-ekyc-next-face]');
        const manualSubmitHost = panel.querySelector('[data-ekyc-manual-submit]');
        let stream = null;
        let manualMode = false;
        let lastFront = null;
        let lastBack = null;

        const showStep = step => {
            panel.querySelectorAll('[data-ekyc-step-pane]').forEach(pane => {
                pane.classList.toggle('d-none', Number(pane.dataset.ekycStepPane) !== step);
            });
            panel.querySelectorAll('[data-ekyc-step-indicator]').forEach(indicator => {
                const number = Number(indicator.dataset.ekycStepIndicator);
                indicator.classList.toggle('is-active', number === step);
                indicator.classList.toggle('is-done', number < step);
            });
            panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
        };

        const stopCamera = () => {
            if (stream) {
                stream.getTracks().forEach(track => track.stop());
                stream = null;
            }
            if (video) video.srcObject = null;
        };

        const resetAutomaticProgress = () => {
            if (manualMode) return;
            if (sessionInput.value) {
                sessionInput.value = '';
                selfieInput.value = '';
                videoState.textContent = 'Chưa có video khuôn mặt.';
                videoState.className = 'small text-muted mt-2';
                submitButton.disabled = true;
                ocrState.textContent = 'Ảnh đã thay đổi, vui lòng đọc CCCD lại.';
                ocrState.className = 'small text-warning mb-3';
            }
        };

        const updateFileState = () => {
            const front = frontInput.files?.[0];
            const back = backInput.files?.[0];
            const changed = front !== lastFront || back !== lastBack;
            lastFront = front || null;
            lastBack = back || null;
            if (changed) resetAutomaticProgress();

            if (front && back) {
                fileState.textContent = `✓ Đã chọn đủ 2 ảnh · ${formatBytes(front.size + back.size)}`;
                fileState.className = 'small text-success mt-2';
                ocrButton.disabled = false;
            } else if (front || back) {
                fileState.textContent = 'Đã chọn 1/2 ảnh. Vui lòng chọn mặt còn lại.';
                fileState.className = 'small text-warning mt-2';
                ocrButton.disabled = true;
            } else {
                fileState.textContent = 'Chưa chọn đủ 2 ảnh CCCD.';
                fileState.className = 'small text-muted mt-2';
                ocrButton.disabled = true;
            }
        };

        frontInput.addEventListener('change', updateFileState);
        backInput.addEventListener('change', updateFileState);
        updateFileState();
        submitButton.disabled = true;

        try {
            const status = await jsonRequest('/Ekyc/Status');
            const badge = panel.querySelector('[data-ekyc-provider]');
            if (badge) {
                badge.textContent = status.isDemo ? 'Chế độ trình diễn' : (status.provider || 'Xác minh giấy tờ điện tử');
                badge.className = `badge ${status.isDemo ? 'bg-warning text-dark' : 'bg-success'}`;
            }
            const demoNotice = panel.querySelector('[data-ekyc-demo-notice]');
            if (status.isDemo && demoNotice) {
                demoNotice.className = 'alert alert-warning py-2 small mb-3';
                demoNotice.innerHTML = '<strong>Chế độ trình diễn:</strong> chưa kết nối AI thật. Kết quả khuôn mặt được mô phỏng và hồ sơ vẫn cần Quản trị viên duyệt.';
            }
            const host = panel.querySelector('[data-ekyc-latest-host]');
            if (host && status.latest) host.innerHTML = latestSummaryHtml(status.latest);
        } catch {
            // Không chặn luồng thủ công khi endpoint trạng thái tạm thời lỗi.
        }

        ocrButton.addEventListener('click', async () => {
            clearMessage(panel);
            const front = frontInput.files?.[0];
            const back = backInput.files?.[0];
            if (!front || !back) {
                setMessage(panel, 'Vui lòng chọn đủ ảnh CCCD mặt trước và mặt sau.', 'warning');
                return;
            }

            if (manualMode) {
                ocrState.textContent = 'Bạn đang nhập thủ công. Hãy đối chiếu từng trường với CCCD.';
                ocrState.className = 'small text-muted mb-3';
                showStep(2);
                return;
            }

            ocrButton.disabled = true;
            const oldText = ocrButton.textContent;
            ocrButton.textContent = 'Đang đọc CCCD...';
            try {
                const data = new FormData();
                addToken(form, data);
                data.append('frontImage', front);
                data.append('backImage', back);
                const result = await jsonRequest('/Ekyc/PreviewCitizenId', { method: 'POST', body: data });

                sessionInput.value = result.sessionId || '';
                setInputValue('[name="CitizenIdVerification.FullNameOnDocument"]', result.fullName);
                setInputValue('[name="CitizenIdVerification.DocumentNumber"]', result.documentNumber);
                setInputValue('[name="CitizenIdVerification.Gender"]', result.gender);
                setInputValue('[name="CitizenIdVerification.PermanentAddress"]', result.address);
                setDateValue('#citizen-birth-display', '#citizen-birth-value', result.dateOfBirth);
                setDateValue('#citizen-issued-display', '#citizen-issued-value', result.issuedDate);
                setDateValue('#citizen-expiry-display', '#citizen-expiry-value', result.expiryDate);

                ocrState.textContent = result.isDemo
                    ? 'Chế độ trình diễn không tự bịa dữ liệu. Vui lòng nhập thông tin đúng như trên CCCD.'
                    : `✓ OCR thành công${result.ocrConfidence != null ? ` · độ tin cậy ${Number(result.ocrConfidence).toFixed(1)}%` : ''}. Hãy kiểm tra lại.`;
                ocrState.className = `small ${result.isDemo ? 'text-warning' : 'text-success'} mb-3`;
                setMessage(panel, result.message || 'Đã đọc CCCD. Vui lòng kiểm tra thông tin.', result.isDemo ? 'warning' : 'success');
                showStep(2);
            } catch (error) {
                sessionInput.value = '';
                setMessage(panel, error.message, 'danger');
            } finally {
                ocrButton.disabled = !(frontInput.files?.[0] && backInput.files?.[0]);
                ocrButton.textContent = oldText;
            }
        });

        panel.querySelector('[data-ekyc-manual]').addEventListener('click', () => {
            manualMode = true;
            sessionInput.value = '';
            selfieInput.value = '';
            stopCamera();
            submitButton.disabled = false;
            ocrButton.textContent = 'Tiếp tục nhập thông tin';
            nextFaceButton.classList.add('d-none');
            if (submitContainer) manualSubmitHost.appendChild(submitContainer);
            manualSubmitHost.classList.remove('d-none');
            setMessage(panel, 'Đã chuyển sang xác minh thủ công. Bạn vẫn chỉ cần chọn mỗi ảnh một lần; Quản trị viên sẽ đối chiếu trực tiếp.', 'secondary');
            showStep(1);
            updateFileState();
        });

        panel.querySelectorAll('[data-ekyc-back]').forEach(button => {
            button.addEventListener('click', () => showStep(Number(button.dataset.ekycBack)));
        });

        nextFaceButton.addEventListener('click', () => {
            if (!validateContainer(fieldsHost)) {
                setMessage(panel, 'Vui lòng hoàn tất và kiểm tra lại thông tin CCCD trước khi xác minh khuôn mặt.', 'warning');
                return;
            }
            clearMessage(panel);
            showStep(3);
        });

        panel.querySelector('[data-ekyc-camera-start]').addEventListener('click', async () => {
            clearMessage(panel);
            if (!navigator.mediaDevices?.getUserMedia) {
                setMessage(panel, 'Trình duyệt không hỗ trợ camera trực tiếp. Hãy dùng “Chọn video có sẵn”.', 'warning');
                return;
            }
            try {
                stopCamera();
                stream = await navigator.mediaDevices.getUserMedia({
                    video: { facingMode: 'user', width: { ideal: 720 }, height: { ideal: 720 } },
                    audio: false
                });
                video.srcObject = stream;
                cameraWrap.classList.remove('d-none');
                cameraPlaceholder.classList.add('d-none');
                recordButton.disabled = false;
                setMessage(panel, 'Camera đã bật. Giữ khuôn mặt trong khung, nhìn thẳng và bấm “Quay 5 giây”.', 'info');
            } catch {
                setMessage(panel, 'Không truy cập được camera. Hãy cấp quyền camera cho trình duyệt hoặc chọn video có sẵn.', 'danger');
            }
        });

        recordButton.addEventListener('click', async () => {
            if (!stream || typeof MediaRecorder === 'undefined') {
                setMessage(panel, 'Không thể quay trực tiếp trên trình duyệt này. Hãy chọn video có sẵn.', 'warning');
                return;
            }

            recordButton.disabled = true;
            clearMessage(panel);
            const chunks = [];
            let recorder;
            try {
                const preferred = ['video/webm;codecs=vp8', 'video/webm', 'video/mp4']
                    .find(type => MediaRecorder.isTypeSupported?.(type));
                recorder = preferred ? new MediaRecorder(stream, { mimeType: preferred }) : new MediaRecorder(stream);
            } catch {
                setMessage(panel, 'Trình duyệt không hỗ trợ định dạng quay video. Hãy chọn video có sẵn.', 'warning');
                recordButton.disabled = false;
                return;
            }

            recorder.addEventListener('dataavailable', event => { if (event.data?.size) chunks.push(event.data); });
            const stopped = new Promise(resolve => recorder.addEventListener('stop', resolve, { once: true }));
            recorder.start();
            videoState.textContent = '● Đang quay... giữ khuôn mặt trong khung.';
            videoState.className = 'small text-danger mt-2';
            await new Promise(resolve => setTimeout(resolve, 5000));
            recorder.stop();
            await stopped;

            const mime = recorder.mimeType || 'video/webm';
            const extension = mime.includes('mp4') ? 'mp4' : 'webm';
            const blob = new Blob(chunks, { type: mime });
            const file = new File([blob], `selfie-${Date.now()}.${extension}`, { type: mime });

            try {
                const transfer = new DataTransfer();
                transfer.items.add(file);
                selfieInput.files = transfer.files;
                selfieInput.dispatchEvent(new Event('change', { bubbles: true }));
            } catch {
                setMessage(panel, 'Video đã quay nhưng trình duyệt không thể gắn file tự động. Hãy dùng “Chọn video có sẵn”.', 'warning');
            }

            stopCamera();
            cameraWrap.classList.add('d-none');
            cameraPlaceholder.classList.remove('d-none');
        });

        panel.querySelector('[data-ekyc-video-upload]').addEventListener('click', () => selfieInput.click());
        selfieInput.addEventListener('change', () => {
            const file = selfieInput.files?.[0];
            if (file) {
                videoState.textContent = `✓ Video khuôn mặt đã sẵn sàng · ${formatBytes(file.size)}`;
                videoState.className = 'small text-success mt-2';
                submitButton.disabled = false;
            } else {
                videoState.textContent = 'Chưa có video khuôn mặt.';
                videoState.className = 'small text-muted mt-2';
                submitButton.disabled = true;
            }
        });

        form.addEventListener('submit', async event => {
            if (!sessionInput.value) return;
            event.preventDefault();
            event.stopImmediatePropagation();

            if (!selfieInput.files?.length) {
                setMessage(panel, 'Vui lòng quay hoặc chọn video khuôn mặt trước khi gửi.', 'warning');
                showStep(3);
                return;
            }
            if (window.jQuery && window.jQuery(form).data('validator') && !window.jQuery(form).valid()) {
                setMessage(panel, 'Vui lòng kiểm tra lại các trường thông tin trước khi gửi.', 'warning');
                return;
            }
            if (!form.checkValidity()) {
                form.reportValidity();
                setMessage(panel, 'Vui lòng kiểm tra lại các trường bắt buộc trước khi gửi.', 'warning');
                return;
            }

            submitButton.disabled = true;
            const originalText = submitButton.textContent;
            submitButton.textContent = 'Đang xác minh khuôn mặt...';
            clearMessage(panel);

            try {
                const result = await jsonRequest('/Ekyc/VerifyAndSubmitCitizenId', {
                    method: 'POST',
                    body: new FormData(form),
                    headers: { 'X-Requested-With': 'XMLHttpRequest' }
                });
                setMessage(panel, result.message || 'Đã gửi hồ sơ xác minh giấy tờ điện tử.', 'success');
                window.location.assign(result.redirectUrl || '/Profile?tab=documents');
            } catch (error) {
                setMessage(panel, error.message, 'danger');
                submitButton.disabled = false;
                submitButton.textContent = originalText || 'Gửi xác minh CCCD';
            }
        }, true);
    };

    const setupLicenseOcr = () => {
        const form = document.querySelector('form[action*="SubmitDrivingLicense"]');
        if (!form || form.querySelector('[data-ekyc-panel="license"]')) return;
        const fieldset = form.querySelector('fieldset');
        if (!fieldset || fieldset.disabled) return;

        const frontInput = document.getElementById('license-front-file');
        const backInput = document.getElementById('license-back-file');
        const submitButton = form.querySelector('button[type="submit"]');
        if (!frontInput || !backInput || !submitButton) return;

        const wrapper = document.createElement('div');
        wrapper.innerHTML = licensePanelHtml().trim();
        const panel = wrapper.firstElementChild;
        form.insertBefore(panel, fieldset);

        const textRow = fieldset.querySelector('.row.g-3');
        const frontCol = frontInput.closest('.col-md-6');
        const backCol = backInput.closest('.col-md-6');
        const submitContainer = submitButton.closest('.d-flex') || submitButton.parentElement;
        const uploadGrid = panel.querySelector('[data-license-upload-grid]');
        const fieldsHost = panel.querySelector('[data-license-form-fields]');
        const submitHost = panel.querySelector('[data-license-submit-host]');

        if (frontCol) { decorateMovedUpload(frontCol, 'Mặt trước GPLX'); uploadGrid.appendChild(frontCol); }
        if (backCol) { decorateMovedUpload(backCol, 'Mặt sau GPLX'); uploadGrid.appendChild(backCol); }
        if (textRow) fieldsHost.appendChild(textRow);
        if (submitContainer) submitHost.appendChild(submitContainer);
        fieldset.classList.add('d-none');

        const ocrButton = panel.querySelector('[data-license-ocr]');
        const fileState = panel.querySelector('[data-license-file-state]');
        let manualMode = false;

        const showStep = step => {
            panel.querySelectorAll('[data-license-step-pane]').forEach(pane => {
                pane.classList.toggle('d-none', Number(pane.dataset.licenseStepPane) !== step);
            });
            panel.querySelectorAll('[data-license-step-indicator]').forEach(indicator => {
                const number = Number(indicator.dataset.licenseStepIndicator);
                indicator.classList.toggle('is-active', number === step);
                indicator.classList.toggle('is-done', number < step);
            });
            panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
        };

        const updateFileState = () => {
            const front = frontInput.files?.[0];
            const back = backInput.files?.[0];
            if (front && back) {
                fileState.textContent = `✓ Đã chọn đủ 2 ảnh · ${formatBytes(front.size + back.size)}`;
                fileState.className = 'small text-success mt-2';
                ocrButton.disabled = false;
            } else if (front || back) {
                fileState.textContent = 'Đã chọn 1/2 ảnh. Vui lòng chọn mặt còn lại.';
                fileState.className = 'small text-warning mt-2';
                ocrButton.disabled = true;
            } else {
                fileState.textContent = 'Chưa chọn đủ 2 ảnh GPLX.';
                fileState.className = 'small text-muted mt-2';
                ocrButton.disabled = true;
            }
        };

        frontInput.addEventListener('change', updateFileState);
        backInput.addEventListener('change', updateFileState);
        updateFileState();

        panel.querySelector('[data-license-manual]').addEventListener('click', () => {
            manualMode = true;
            ocrButton.textContent = 'Tiếp tục nhập thông tin';
            setLicenseMessage(panel, 'Đã chuyển sang nhập thủ công. Hai ảnh bạn chọn vẫn được dùng trực tiếp trong hồ sơ.', 'secondary');
        });

        ocrButton.addEventListener('click', async () => {
            const front = frontInput.files?.[0];
            const back = backInput.files?.[0];
            if (!front || !back) {
                setLicenseMessage(panel, 'Vui lòng chọn đủ ảnh GPLX mặt trước và mặt sau.', 'warning');
                return;
            }

            if (manualMode) {
                showStep(2);
                return;
            }

            ocrButton.disabled = true;
            const oldText = ocrButton.textContent;
            ocrButton.textContent = 'Đang đọc GPLX...';
            try {
                const data = new FormData();
                addToken(form, data);
                data.append('frontImage', front);
                data.append('backImage', back);
                const result = await jsonRequest('/Ekyc/PreviewDrivingLicense', { method: 'POST', body: data });

                setInputValue('[name="DrivingLicenseVerification.FullNameOnDocument"]', result.fullName);
                setInputValue('[name="DrivingLicenseVerification.DocumentNumber"]', result.documentNumber);
                setInputValue('[name="DrivingLicenseVerification.LicenseClass"]', result.licenseClass?.toUpperCase());
                setDateValue('#license-issued-display', '#license-issued-value', result.issuedDate);
                setDateValue('#license-expiry-display', '#license-expiry-value', result.expiryDate);

                setLicenseMessage(panel,
                    result.isDemo
                        ? 'Chế độ trình diễn không tự điền dữ liệu giả. Vui lòng nhập đúng thông tin trên GPLX.'
                        : (result.message || 'Đã đọc GPLX. Vui lòng kiểm tra lại thông tin.'),
                    result.isDemo ? 'warning' : 'success');
                showStep(2);
            } catch (error) {
                setLicenseMessage(panel, error.message, 'danger');
            } finally {
                ocrButton.disabled = !(frontInput.files?.[0] && backInput.files?.[0]);
                ocrButton.textContent = oldText;
            }
        });

        panel.querySelector('[data-license-back]').addEventListener('click', () => showStep(1));
    };

    const setupAdminSummary = async () => {
        if (!/\/AdminCustomers\/Details/i.test(window.location.pathname)) return;
        const customerId = document.querySelector('input[name="customerId"]')?.value;
        if (!customerId) return;

        try {
            const payload = await jsonRequest(`/Ekyc/AdminSummary?customerId=${encodeURIComponent(customerId)}`);
            const result = payload?.result;
            if (!result) return;
            const sections = [...document.querySelectorAll('section.card')];
            const citizenSection = sections.find(section => section.textContent?.includes('Xác minh danh tính — CCCD'));
            if (!citizenSection || document.querySelector('[data-admin-ekyc-summary]')) return;

            const similarity = result.faceSimilarity == null ? 'N/A' : `${Number(result.faceSimilarity).toFixed(1)}%`;
            const card = document.createElement('section');
            card.className = 'card border-0 shadow-sm mb-4';
            card.dataset.adminEkycSummary = '';
            card.innerHTML = `
                <div class="card-body p-4">
                    <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap mb-3">
                        <div><div class="small text-uppercase text-primary fw-bold">Kết quả xác minh điện tử hỗ trợ duyệt</div><h3 class="h5 fw-bold mb-1">OCR + Liveness + Face Match</h3><div class="small text-muted">${result.provider}${result.isDemo ? ' · Chế độ trình diễn' : ''}</div></div>
                        <span class="badge ${result.livenessPassed === true && result.faceMatched === true && !result.isDemo ? 'bg-success' : 'bg-warning text-dark'}">${result.isDemo ? 'Kết quả mô phỏng' : result.livenessPassed === true && result.faceMatched === true ? 'AI đạt' : 'Cần kiểm tra'}</span>
                    </div>
                    <div class="row g-3">
                        <div class="col-md-3"><div class="border rounded p-3 h-100"><span class="small text-muted d-block">OCR</span><strong>${result.ocrSucceeded ? 'Đạt' : 'Không đạt'}</strong></div></div>
                        <div class="col-md-3"><div class="border rounded p-3 h-100"><span class="small text-muted d-block">Người thật</span><strong>${result.livenessPassed === true ? 'Đạt' : result.livenessPassed === false ? 'Không đạt' : 'N/A'}</strong></div></div>
                        <div class="col-md-3"><div class="border rounded p-3 h-100"><span class="small text-muted d-block">Khớp khuôn mặt</span><strong>${result.faceMatched === true ? 'Đạt' : result.faceMatched === false ? 'Không đạt' : 'N/A'}</strong></div></div>
                        <div class="col-md-3"><div class="border rounded p-3 h-100"><span class="small text-muted d-block">Độ tương đồng</span><strong>${similarity}</strong></div></div>
                    </div>
                    <div class="alert alert-light border small mt-3 mb-0"><strong>Lưu ý:</strong> AI là dữ liệu hỗ trợ. Quản trị viên vẫn phải đối chiếu giấy tờ trước khi xác minh.</div>
                </div>`;
            citizenSection.insertAdjacentElement('beforebegin', card);
        } catch {
            // Admin vẫn có thể duyệt thủ công nếu không tải được kết quả eKYC.
        }
    };

    const init = () => {
        ensureStylesheet();
        setupCitizenEkyc();
        setupLicenseOcr();
        setupAdminSummary();
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }
})();
