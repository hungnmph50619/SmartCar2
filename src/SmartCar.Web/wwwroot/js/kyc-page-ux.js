(() => {
    const textIncludes = (element, value) => (element?.textContent || '').toLowerCase().includes(value.toLowerCase());

    const findLicenseSection = () => {
        const panel = document.querySelector('[data-ekyc-panel="license"]');
        if (panel) return panel.closest('section.card') || panel.parentElement;
        return [...document.querySelectorAll('section.card')]
            .find(section => textIncludes(section, 'Thông tin giấy phép lái xe')) || null;
    };

    const collapsePendingCitizen = citizenSection => {
        if (!citizenSection || citizenSection.dataset.pendingCollapsed === 'true') return;
        if (!textIncludes(citizenSection, 'Hồ sơ CCCD đang chờ xác minh')) return;

        const fieldset = citizenSection.querySelector('fieldset[disabled]');
        if (!fieldset) return;
        fieldset.classList.add('d-none');

        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'btn btn-sm btn-outline-secondary mb-3';
        toggle.textContent = 'Xem lại thông tin CCCD đã gửi';
        toggle.addEventListener('click', () => {
            const opening = fieldset.classList.contains('d-none');
            fieldset.classList.toggle('d-none', !opening);
            toggle.textContent = opening ? 'Thu gọn CCCD đã gửi' : 'Xem lại thông tin CCCD đã gửi';
        });

        const alert = [...citizenSection.querySelectorAll('.alert')]
            .find(item => textIncludes(item, 'đang chờ xác minh'));
        alert?.insertAdjacentElement('afterend', toggle);
        citizenSection.dataset.pendingCollapsed = 'true';
    };

    const resetEmptyLicenseStates = () => {
        const panel = document.querySelector('[data-ekyc-panel="license"]');
        if (!panel) return;
        const front = document.getElementById('license-front-file');
        const back = document.getElementById('license-back-file');
        const frontChosen = Boolean(front?.files?.length);
        const backChosen = Boolean(back?.files?.length);
        if (frontChosen && backChosen) return;

        const message = panel.querySelector('[data-license-message]');
        if (message && /ảnh chưa đạt|chưa cho gửi hồ sơ|chụp lại ảnh/i.test(message.textContent || '')) {
            message.classList.add('d-none');
            message.textContent = '';
        }

        const strictHost = panel.querySelector('[data-strict-quality-host]');
        if (strictHost) {
            const badge = strictHost.querySelector('[data-strict-quality-badge]');
            const body = strictHost.querySelector('[data-strict-quality-body]');
            if (badge) {
                badge.textContent = frontChosen || backChosen ? 'Chưa đủ ảnh' : 'Chưa kiểm tra';
                badge.className = `badge ${frontChosen || backChosen ? 'bg-warning text-dark' : 'bg-secondary'}`;
            }
            if (body) {
                body.className = 'small text-muted';
                body.textContent = frontChosen || backChosen
                    ? 'Đã chọn 1/2 ảnh. Hãy chọn nốt mặt còn lại trước khi kiểm tra chất lượng.'
                    : 'Chọn đủ mặt trước và mặt sau. SmartCar chỉ kiểm tra chất lượng sau khi bạn đã chọn ảnh.';
            }
        }

        const qualityHost = panel.querySelector('[data-image-quality-host], [data-quality-host]');
        if (qualityHost && !frontChosen && !backChosen) {
            const badge = qualityHost.querySelector('.badge');
            if (badge) {
                badge.textContent = 'Chưa kiểm tra';
                badge.className = 'badge bg-secondary';
            }
        }
    };

    const installGuide = () => {
        const citizenSection = document.getElementById('citizen-id-verification');
        const licenseSection = findLicenseSection();
        if (!citizenSection || !licenseSection || document.querySelector('[data-kyc-flow-guide]')) return;

        const citizenPending = textIncludes(citizenSection, 'Chờ xác minh') || textIncludes(citizenSection, 'đang chờ xác minh');
        const citizenVerified = textIncludes(citizenSection, 'Đã xác minh');
        const licensePending = textIncludes(licenseSection, 'Chờ xác minh') || textIncludes(licenseSection, 'đang chờ xác minh');
        const licenseVerified = textIncludes(licenseSection, 'Đã xác minh');

        let stateText = 'Hoàn tất CCCD trước, sau đó hoàn tất GPLX. SmartCar chỉ gửi hồ sơ cho Quản trị viên xử lý khi đã đủ cả hai loại giấy tờ.';
        let stateClass = 'alert-primary';
        let showContinue = true;

        if (citizenPending && !licensePending && !licenseVerified) {
            stateText = '✓ CCCD đã gửi. Bạn không cần chờ Quản trị viên duyệt CCCD riêng. Hãy tiếp tục GPLX; khi đủ CCCD + GPLX, Quản trị viên mới nhận một yêu cầu để xem và duyệt toàn bộ KYC.';
            stateClass = 'alert-info';
        } else if ((citizenPending || citizenVerified) && (licensePending || licenseVerified)) {
            stateText = '✓ Bạn đã hoàn tất cả CCCD và GPLX. Quản trị viên sẽ đối chiếu hai giấy tờ trong cùng một hồ sơ và duyệt một lần. Bạn chỉ cần chờ kết quả.';
            stateClass = 'alert-success';
            showContinue = false;
        }

        const guide = document.createElement('div');
        guide.dataset.kycFlowGuide = '';
        guide.className = 'card border-0 shadow-sm mb-4';
        guide.innerHTML = `
            <div class="card-body p-3 p-md-4">
                <div class="d-flex justify-content-between align-items-start gap-3 flex-wrap mb-3">
                    <div>
                        <div class="text-uppercase small text-primary fw-semibold">CÁCH HOÀN TẤT KYC</div>
                        <h3 class="h5 fw-bold mb-1">3 bước, Quản trị viên chỉ duyệt một lần</h3>
                    </div>
                    <span class="badge bg-light text-dark border">CCCD + GPLX</span>
                </div>
                <div class="row g-2 mb-3">
                    <div class="col-md-4"><div class="border rounded-3 p-3 h-100"><strong>1. CCCD</strong><div class="small text-muted mt-1">Chọn 2 mặt → kiểm tra ảnh → đọc QR/MRZ → kiểm tra thông tin.</div></div></div>
                    <div class="col-md-4"><div class="border rounded-3 p-3 h-100"><strong>2. GPLX</strong><div class="small text-muted mt-1">Chọn 2 mặt → kiểm tra ảnh → ưu tiên QR, OCR bổ sung → kiểm tra thông tin.</div></div></div>
                    <div class="col-md-4"><div class="border rounded-3 p-3 h-100"><strong>3. Duyệt KYC</strong><div class="small text-muted mt-1">Khi đủ hai giấy tờ, Admin xem cả 4 ảnh và duyệt toàn bộ hồ sơ một lần.</div></div></div>
                </div>
                <div class="alert ${stateClass} mb-0" data-kyc-flow-state>${stateText}</div>
            </div>`;

        citizenSection.insertAdjacentElement('beforebegin', guide);

        if (showContinue) {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'btn btn-primary btn-sm mt-3';
            button.textContent = citizenPending ? 'Tiếp tục bước GPLX ↓' : 'Bắt đầu xác minh ↓';
            button.addEventListener('click', () => {
                (citizenPending ? licenseSection : citizenSection).scrollIntoView({ behavior: 'smooth', block: 'start' });
            });
            guide.querySelector('[data-kyc-flow-state]')?.append(document.createElement('br'), button);
        }

        collapsePendingCitizen(citizenSection);
        resetEmptyLicenseStates();
    };

    const init = () => {
        installGuide();
        resetEmptyLicenseStates();

        const licensePanel = document.querySelector('[data-ekyc-panel="license"]');
        if (licensePanel) {
            new MutationObserver(() => resetEmptyLicenseStates())
                .observe(licensePanel, { childList: true, subtree: true, characterData: true });
        }
        document.getElementById('license-front-file')?.addEventListener('change', resetEmptyLicenseStates);
        document.getElementById('license-back-file')?.addEventListener('change', resetEmptyLicenseStates);
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init, { once: true });
    } else {
        init();
    }
})();
