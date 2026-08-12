(() => {
    const state = {
        available: false,
        active: false
    };
    window.SmartCarKycTest = state;

    const getToken = () => document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

    const json = async (url, options = {}) => {
        const response = await fetch(url, { credentials: 'same-origin', ...options });
        let payload = null;
        try { payload = await response.json(); } catch { payload = null; }
        if (!response.ok) throw new Error(payload?.message || 'Không thể cập nhật chế độ kiểm thử xác minh giấy tờ.');
        return payload || {};
    };

    const setMode = async enabled => {
        const data = new FormData();
        const token = getToken();
        if (token) data.append('__RequestVerificationToken', token);
        data.append('enabled', enabled ? 'true' : 'false');
        await json('/Ekyc/SetTestMode', { method: 'POST', body: data });
        window.location.reload();
    };

    const canvasToFile = (canvas, name) => new Promise((resolve, reject) => {
        canvas.toBlob(blob => {
            if (!blob) {
                reject(new Error('Không thể tạo ảnh mẫu.'));
                return;
            }
            resolve(new File([blob], name, { type: 'image/png', lastModified: Date.now() }));
        }, 'image/png');
    });

    const drawQrLike = (ctx, x, y, size) => {
        const cells = 17;
        const cell = size / cells;
        ctx.save();
        ctx.fillStyle = '#fff';
        ctx.fillRect(x - 8, y - 8, size + 16, size + 16);
        ctx.fillStyle = '#111';
        for (let row = 0; row < cells; row += 1) {
            for (let col = 0; col < cells; col += 1) {
                const finder = (row < 5 && col < 5) || (row < 5 && col >= cells - 5) || (row >= cells - 5 && col < 5);
                const pattern = finder || ((row * 7 + col * 11 + row * col) % 5 < 2);
                if (pattern) ctx.fillRect(x + col * cell, y + row * cell, Math.ceil(cell), Math.ceil(cell));
            }
        }
        ctx.restore();
    };

    const makeCard = async (kind, side) => {
        const canvas = document.createElement('canvas');
        canvas.width = 1000;
        canvas.height = 630;
        const ctx = canvas.getContext('2d');
        ctx.fillStyle = kind === 'citizen' ? '#e8f6f5' : '#fff8df';
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        ctx.strokeStyle = '#27364a';
        ctx.lineWidth = 8;
        ctx.strokeRect(8, 8, canvas.width - 16, canvas.height - 16);

        ctx.fillStyle = '#c62828';
        ctx.font = '700 34px Arial, sans-serif';
        ctx.fillText('SMARTCAR DEMO — KHÔNG CÓ GIÁ TRỊ', 48, 58);
        ctx.fillStyle = '#132238';

        if (kind === 'citizen' && side === 'front') {
            ctx.font = '700 44px Arial, sans-serif';
            ctx.fillText('CĂN CƯỚC CÔNG DÂN', 48, 125);
            ctx.font = '28px Arial, sans-serif';
            ctx.fillText('Số / No: 099999999999', 48, 190);
            ctx.fillText('Họ và tên / Full name: NGUYỄN VĂN TEST', 48, 240);
            ctx.fillText('Ngày sinh / Date of birth: 15/05/1998', 48, 290);
            ctx.fillText('Giới tính / Sex: Nam', 48, 340);
            ctx.fillText('Nơi cư trú / Place of residence:', 48, 390);
            ctx.fillText('123 Đường Test, TP. Ninh Bình, Ninh Bình', 48, 430);
            ctx.fillText('Có giá trị đến / Date of expiry: 15/05/2040', 48, 485);
            drawQrLike(ctx, 785, 120, 155);
        } else if (kind === 'citizen') {
            ctx.font = '700 34px Arial, sans-serif';
            ctx.fillText('ĐẶC ĐIỂM NHẬN DẠNG / PERSONAL IDENTIFICATION', 48, 130);
            ctx.font = '26px Arial, sans-serif';
            ctx.fillText('Ngày cấp / Date of issue: 01/01/2025', 48, 185);
            ctx.fillText('Ngón trỏ trái / LEFT INDEX', 48, 240);
            ctx.fillText('Ngón trỏ phải / RIGHT INDEX', 500, 240);
            ctx.font = '700 31px Consolas, monospace';
            ctx.fillText('IDVNM099999999999<<<<<<<<<<<<', 48, 400);
            ctx.fillText('9805158M4005157VNM<<<<<<<<<<4', 48, 448);
            ctx.fillText('NGUYEN<<VAN<TEST<<<<<<<<<<<<', 48, 496);
        } else if (side === 'front') {
            ctx.font = '700 44px Arial, sans-serif';
            ctx.fillText('GIẤY PHÉP LÁI XE / DRIVER’S LICENSE', 48, 125);
            ctx.font = '28px Arial, sans-serif';
            ctx.fillText('Số / No: TESTB123456', 48, 195);
            ctx.fillText('Họ và tên / Full name: NGUYỄN VĂN TEST', 48, 245);
            ctx.fillText('Ngày sinh / Date of birth: 15/05/1998', 48, 295);
            ctx.fillText('Hạng / Class: B', 48, 345);
            ctx.fillText('Ngày cấp / Date of issue: 02/01/2025', 48, 395);
            ctx.fillText('Có giá trị đến / Date of expiry: 15/05/2035', 48, 445);
        } else {
            ctx.font = '700 39px Arial, sans-serif';
            ctx.fillText('CÁC LOẠI XE CƠ GIỚI ĐƯỢC ĐIỀU KHIỂN', 48, 130);
            ctx.font = '700 29px Arial, sans-serif';
            ctx.fillText('CLASSIFICATION OF MOTOR VEHICLES', 48, 180);
            ctx.font = '27px Arial, sans-serif';
            ctx.fillText('Hạng / Class: B', 48, 245);
            ctx.fillText('Ghi chú / Notes: SMARTCAR TEST DATA', 48, 300);
            drawQrLike(ctx, 770, 340, 165);
        }

        ctx.fillStyle = 'rgba(198, 40, 40, 0.14)';
        ctx.font = '700 74px Arial, sans-serif';
        ctx.save();
        ctx.translate(500, 560);
        ctx.rotate(-0.13);
        ctx.textAlign = 'center';
        ctx.fillText('DEMO / SAMPLE', 0, 0);
        ctx.restore();

        return canvasToFile(canvas, `smartcar-test-${kind}-${side}.png`);
    };

    const assignFile = (input, file) => {
        if (!input || !window.DataTransfer) return;
        const transfer = new DataTransfer();
        transfer.items.add(file);
        input.files = transfer.files;
        input.dispatchEvent(new Event('change', { bubbles: true }));
    };

    const ensureFakeSelfieVideo = () => {
        const input = document.querySelector('[data-ekyc-video-input]');
        if (!input || input.files?.length || !window.DataTransfer) return;
        const blob = new Blob(['SMARTCAR DEVELOPMENT KYC TEST VIDEO'], { type: 'video/webm' });
        const file = new File([blob], 'smartcar-test-selfie.webm', { type: 'video/webm', lastModified: Date.now() });
        assignFile(input, file);
    };

    const loadCitizenSample = async button => {
        button.disabled = true;
        const old = button.textContent;
        button.textContent = 'Đang tạo bộ CCCD mẫu...';
        try {
            const [front, back] = await Promise.all([
                makeCard('citizen', 'front'),
                makeCard('citizen', 'back')
            ]);
            assignFile(document.getElementById('citizen-front-file'), front);
            assignFile(document.getElementById('citizen-back-file'), back);
            ensureFakeSelfieVideo();
        } finally {
            button.disabled = false;
            button.textContent = old;
        }
    };

    const loadLicenseSample = async button => {
        button.disabled = true;
        const old = button.textContent;
        button.textContent = 'Đang tạo bộ GPLX mẫu...';
        try {
            const [front, back] = await Promise.all([
                makeCard('license', 'front'),
                makeCard('license', 'back')
            ]);
            assignFile(document.getElementById('license-front-file'), front);
            assignFile(document.getElementById('license-back-file'), back);
        } finally {
            button.disabled = false;
            button.textContent = old;
        }
    };

    const installPanelButtons = () => {
        if (!state.active) return;
        const citizen = document.querySelector('[data-ekyc-panel="citizen"]');
        if (citizen && !citizen.querySelector('[data-kyc-test-citizen]')) {
            const actions = citizen.querySelector('[data-ekyc-step-pane="1"] .ekyc-actions');
            if (actions) {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'btn btn-outline-warning';
                button.dataset.kycTestCitizen = '';
                button.textContent = '🧪 Dùng bộ CCCD mẫu';
                button.addEventListener('click', () => void loadCitizenSample(button));
                actions.appendChild(button);
            }
        }

        const license = document.querySelector('[data-ekyc-panel="license"]');
        if (license && !license.querySelector('[data-kyc-test-license]')) {
            const actions = license.querySelector('[data-license-step-pane="1"] .ekyc-actions');
            if (actions) {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'btn btn-outline-warning';
                button.dataset.kycTestLicense = '';
                button.textContent = '🧪 Dùng bộ GPLX mẫu';
                button.addEventListener('click', () => void loadLicenseSample(button));
                actions.appendChild(button);
            }
        }
    };

    const installBanner = () => {
        if (!state.available || document.querySelector('[data-kyc-test-banner]')) return;
        const target = document.querySelector('[data-ekyc-panel="citizen"], [data-ekyc-panel="license"]');
        if (!target) return;

        const banner = document.createElement('div');
        banner.dataset.kycTestBanner = '';
        banner.className = `alert ${state.active ? 'alert-warning' : 'alert-secondary'} border shadow-sm`;
        banner.innerHTML = `
            <div class="d-flex justify-content-between gap-3 align-items-center flex-wrap">
                <div>
                    <strong>${state.active ? '🧪 Chế độ kiểm thử xác minh giấy tờ đang BẬT' : '🧪 Có chế độ kiểm thử xác minh giấy tờ'}</strong>
                    <div class="small mt-1">
                        ${state.active
                            ? 'Chỉ trong Development: nhận diện/đọc dữ liệu mẫu được mô phỏng để test luồng. Validation nghiệp vụ, lưu hồ sơ, thông báo và Admin duyệt vẫn chạy bình thường.'
                            : 'Chỉ dùng trong Development để nhóm test luồng bằng dữ liệu giả. Chế độ bình thường vẫn giữ kiểm tra giấy tờ nghiêm ngặt.'}
                    </div>
                </div>
                <button type="button" class="btn ${state.active ? 'btn-outline-dark' : 'btn-warning'}" data-kyc-test-toggle>
                    ${state.active ? 'Tắt chế độ test' : 'Bật chế độ test'}
                </button>
            </div>`;
        target.insertAdjacentElement('beforebegin', banner);
        banner.querySelector('[data-kyc-test-toggle]')?.addEventListener('click', event => {
            event.currentTarget.disabled = true;
            void setMode(!state.active).catch(error => {
                event.currentTarget.disabled = false;
                window.alert(error.message);
            });
        });
    };

    const install = () => {
        installBanner();
        installPanelButtons();
    };

    const init = async () => {
        try {
            const status = await json('/Ekyc/TestModeStatus');
            state.available = status.available === true;
            state.active = status.active === true;
        } catch {
            state.available = false;
            state.active = false;
        }
        install();
        new MutationObserver(install).observe(document.documentElement, { childList: true, subtree: true });
    };

    void init();
})();
