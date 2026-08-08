(() => {
    const sameFileContent = async (first, second) => {
        if (!first || !second || first.size !== second.size) return false;
        if (first === second) return true;

        if (window.crypto?.subtle) {
            const [firstBuffer, secondBuffer] = await Promise.all([
                first.arrayBuffer(),
                second.arrayBuffer()
            ]);
            const [firstHash, secondHash] = await Promise.all([
                window.crypto.subtle.digest('SHA-256', firstBuffer),
                window.crypto.subtle.digest('SHA-256', secondBuffer)
            ]);
            const left = new Uint8Array(firstHash);
            const right = new Uint8Array(secondHash);
            if (left.length !== right.length) return false;
            for (let index = 0; index < left.length; index += 1) {
                if (left[index] !== right[index]) return false;
            }
            return true;
        }

        return first.name === second.name &&
            first.size === second.size &&
            first.lastModified === second.lastModified;
    };

    const setCitizenMessage = (panel, message, type = 'warning') => {
        const box = panel.querySelector('[data-ekyc-message]');
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = message;
        box.classList.remove('d-none');
    };

    const setLicenseMessage = (panel, message, type = 'warning') => {
        const box = panel.querySelector('[data-license-message]');
        if (!box) return;
        box.className = `alert alert-${type} ekyc-message mb-3`;
        box.textContent = message;
        box.classList.remove('d-none');
    };

    const showCitizenStep = (panel, step) => {
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

    const showLicenseStep = (panel, step) => {
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

    const installCitizenGuard = () => {
        const form = document.querySelector('form[action*="SubmitCitizenId"]');
        const panel = form?.querySelector('[data-ekyc-panel="citizen"]');
        if (!form || !panel || panel.dataset.guardInstalled === 'true') return;
        panel.dataset.guardInstalled = 'true';

        const frontInput = document.getElementById('citizen-front-file');
        const backInput = document.getElementById('citizen-back-file');
        const fileState = panel.querySelector('[data-ekyc-file-state]');
        const ocrButton = panel.querySelector('[data-ekyc-ocr]');
        const manualButton = panel.querySelector('[data-ekyc-manual]');
        const nextFaceButton = panel.querySelector('[data-ekyc-next-face]');
        const sessionInput = panel.querySelector('[data-ekyc-session]');
        const selfieInput = panel.querySelector('[data-ekyc-video-input]');
        const submitButton = form.querySelector('button[type="submit"]');
        const submitHost = panel.querySelector('[data-ekyc-submit-host]');
        const manualSubmitHost = panel.querySelector('[data-ekyc-manual-submit]');
        const submitContainer = submitButton?.closest('.d-flex') || submitButton?.parentElement;
        const ocrState = panel.querySelector('[data-ekyc-ocr-state]');
        if (!frontInput || !backInput || !fileState || !ocrButton || !manualButton ||
            !nextFaceButton || !sessionInput || !selfieInput || !submitButton ||
            !submitHost || !manualSubmitHost || !submitContainer) return;

        let validationVersion = 0;

        const checkPair = async ({ updateUi = true } = {}) => {
            const front = frontInput.files?.[0];
            const back = backInput.files?.[0];
            if (!front || !back) {
                panel.dataset.duplicateImages = 'false';
                return false;
            }

            const version = ++validationVersion;
            if (updateUi) {
                fileState.textContent = 'Đang kiểm tra hai mặt giấy tờ...';
                fileState.className = 'small text-muted mt-2';
                ocrButton.disabled = true;
            }

            const duplicated = await sameFileContent(front, back);
            if (version !== validationVersion) return panel.dataset.duplicateImages === 'true';

            panel.dataset.duplicateImages = duplicated ? 'true' : 'false';
            if (duplicated) {
                if (updateUi) {
                    fileState.textContent = '✕ Mặt trước và mặt sau đang là cùng một ảnh. Hãy chọn đúng hai mặt khác nhau.';
                    fileState.className = 'small text-danger fw-semibold mt-2';
                }
                ocrButton.disabled = true;
                submitButton.disabled = true;
                return true;
            }

            if (updateUi) {
                fileState.textContent = `✓ Đã chọn đúng 2 ảnh khác nhau · ${Math.round((front.size + back.size) / 1024)} KB`;
                fileState.className = 'small text-success mt-2';
            }
            ocrButton.disabled = false;
            if (panel.dataset.manualMode === 'true') submitButton.disabled = false;
            return false;
        };

        frontInput.addEventListener('change', () => { void checkPair(); });
        backInput.addEventListener('change', () => { void checkPair(); });
        void checkPair();

        const rejectDuplicate = async () => {
            if (await checkPair()) {
                setCitizenMessage(
                    panel,
                    'Mặt trước và mặt sau CCCD không được dùng cùng một ảnh. Vui lòng chọn đúng hai mặt rồi tiếp tục.',
                    'danger');
                showCitizenStep(panel, 1);
                return true;
            }
            return false;
        };

        ocrButton.addEventListener('click', async event => {
            if (await rejectDuplicate()) {
                event.preventDefault();
                event.stopImmediatePropagation();
            }
        }, true);

        const autoButton = document.createElement('button');
        autoButton.type = 'button';
        autoButton.className = 'btn btn-outline-primary d-none';
        autoButton.textContent = '↻ Quay lại xác minh tự động';
        autoButton.dataset.ekycBackAutomatic = '';
        const stepTwoActions = panel.querySelector('[data-ekyc-step-pane="2"] .ekyc-actions');
        stepTwoActions?.prepend(autoButton);

        manualButton.addEventListener('click', async event => {
            event.preventDefault();
            event.stopImmediatePropagation();

            if (await rejectDuplicate()) return;

            panel.dataset.manualMode = 'true';
            sessionInput.value = '';
            selfieInput.value = '';
            submitButton.disabled = false;
            nextFaceButton.classList.add('d-none');
            autoButton.classList.remove('d-none');
            if (submitContainer.parentElement !== manualSubmitHost) {
                manualSubmitHost.appendChild(submitContainer);
            }
            manualSubmitHost.classList.remove('d-none');
            if (ocrState) {
                ocrState.textContent = 'Bạn đang nhập thủ công. Hãy đối chiếu từng trường với CCCD.';
                ocrState.className = 'small text-muted mb-3';
            }
            setCitizenMessage(
                panel,
                'Đã chuyển sang xác minh thủ công. Ảnh đã chọn vẫn được giữ nguyên; Quản trị viên sẽ đối chiếu trực tiếp.',
                'secondary');
            showCitizenStep(panel, 2);
        }, true);

        autoButton.addEventListener('click', () => {
            panel.dataset.manualMode = 'false';
            sessionInput.value = '';
            selfieInput.value = '';
            submitButton.disabled = true;
            nextFaceButton.classList.remove('d-none');
            autoButton.classList.add('d-none');
            if (submitContainer.parentElement !== submitHost) {
                submitHost.appendChild(submitContainer);
            }
            manualSubmitHost.classList.add('d-none');
            if (ocrState) {
                ocrState.textContent = '';
                ocrState.className = 'small mb-3';
            }
            setCitizenMessage(
                panel,
                'Đã quay lại xác minh tự động. Bấm “Đọc CCCD và tiếp tục” để tạo phiên OCR mới.',
                'info');
            showCitizenStep(panel, 1);
            void checkPair();
        });

        form.addEventListener('submit', event => {
            if (panel.dataset.duplicateImages !== 'true') return;
            event.preventDefault();
            event.stopImmediatePropagation();
            setCitizenMessage(
                panel,
                'Không thể gửi hồ sơ vì mặt trước và mặt sau CCCD đang là cùng một ảnh.',
                'danger');
            showCitizenStep(panel, 1);
        }, true);
    };

    const installLicenseGuard = () => {
        const form = document.querySelector('form[action*="SubmitDrivingLicense"]');
        const panel = form?.querySelector('[data-ekyc-panel="license"]');
        if (!form || !panel || panel.dataset.guardInstalled === 'true') return;
        panel.dataset.guardInstalled = 'true';

        const frontInput = document.getElementById('license-front-file');
        const backInput = document.getElementById('license-back-file');
        const fileState = panel.querySelector('[data-license-file-state]');
        const ocrButton = panel.querySelector('[data-license-ocr]');
        const manualButton = panel.querySelector('[data-license-manual]');
        const submitButton = form.querySelector('button[type="submit"]');
        if (!frontInput || !backInput || !fileState || !ocrButton || !manualButton || !submitButton) return;

        let validationVersion = 0;

        const checkPair = async ({ updateUi = true } = {}) => {
            const front = frontInput.files?.[0];
            const back = backInput.files?.[0];
            if (!front || !back) {
                panel.dataset.duplicateImages = 'false';
                return false;
            }

            const version = ++validationVersion;
            if (updateUi) {
                fileState.textContent = 'Đang kiểm tra hai mặt giấy phép...';
                fileState.className = 'small text-muted mt-2';
                ocrButton.disabled = true;
            }

            const duplicated = await sameFileContent(front, back);
            if (version !== validationVersion) return panel.dataset.duplicateImages === 'true';

            panel.dataset.duplicateImages = duplicated ? 'true' : 'false';
            if (duplicated) {
                if (updateUi) {
                    fileState.textContent = '✕ Mặt trước và mặt sau đang là cùng một ảnh. Hãy chọn đúng hai mặt khác nhau.';
                    fileState.className = 'small text-danger fw-semibold mt-2';
                }
                ocrButton.disabled = true;
                submitButton.disabled = true;
                return true;
            }

            if (updateUi) {
                fileState.textContent = `✓ Đã chọn đúng 2 ảnh khác nhau · ${Math.round((front.size + back.size) / 1024)} KB`;
                fileState.className = 'small text-success mt-2';
            }
            ocrButton.disabled = false;
            return false;
        };

        frontInput.addEventListener('change', () => { void checkPair(); });
        backInput.addEventListener('change', () => { void checkPair(); });
        void checkPair();

        const rejectDuplicate = async () => {
            if (await checkPair()) {
                setLicenseMessage(
                    panel,
                    'Mặt trước và mặt sau GPLX không được dùng cùng một ảnh. Vui lòng chọn đúng hai mặt.',
                    'danger');
                showLicenseStep(panel, 1);
                return true;
            }
            return false;
        };

        ocrButton.addEventListener('click', async event => {
            if (await rejectDuplicate()) {
                event.preventDefault();
                event.stopImmediatePropagation();
            }
        }, true);

        const autoButton = document.createElement('button');
        autoButton.type = 'button';
        autoButton.className = 'btn btn-outline-primary d-none';
        autoButton.textContent = '↻ Quay lại đọc GPLX tự động';
        const stepTwoActions = panel.querySelector('[data-license-step-pane="2"] .ekyc-actions');
        stepTwoActions?.prepend(autoButton);

        manualButton.addEventListener('click', async event => {
            event.preventDefault();
            event.stopImmediatePropagation();
            if (await rejectDuplicate()) return;

            panel.dataset.manualMode = 'true';
            autoButton.classList.remove('d-none');
            setLicenseMessage(
                panel,
                'Đã chuyển sang nhập thủ công. Hai ảnh đã chọn vẫn được giữ nguyên trong hồ sơ.',
                'secondary');
            showLicenseStep(panel, 2);
        }, true);

        autoButton.addEventListener('click', () => {
            panel.dataset.manualMode = 'false';
            autoButton.classList.add('d-none');
            setLicenseMessage(
                panel,
                'Đã quay lại OCR tự động. Bấm “Đọc GPLX và tiếp tục” để đọc lại giấy phép.',
                'info');
            showLicenseStep(panel, 1);
            void checkPair();
        });

        form.addEventListener('submit', event => {
            if (panel.dataset.duplicateImages !== 'true') return;
            event.preventDefault();
            event.stopImmediatePropagation();
            setLicenseMessage(
                panel,
                'Không thể gửi hồ sơ vì mặt trước và mặt sau GPLX đang là cùng một ảnh.',
                'danger');
            showLicenseStep(panel, 1);
        }, true);
    };

    const install = () => {
        installCitizenGuard();
        installLicenseGuard();
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => setTimeout(install, 0), { once: true });
    } else {
        setTimeout(install, 0);
    }
})();
