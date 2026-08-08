(() => {
    const apply = () => {
        const panel = document.querySelector('[data-ekyc-panel="citizen"]');
        if (!panel) return;

        const badge = panel.querySelector('[data-ekyc-provider]');
        if (badge && badge.textContent !== 'QR + MRZ · LOCAL') {
            badge.textContent = 'QR + MRZ · LOCAL';
            badge.className = 'badge bg-info text-dark';
        }

        const notice = panel.querySelector('[data-ekyc-demo-notice]');
        if (notice) {
            notice.className = 'alert alert-info py-2 small mb-3';
            notice.innerHTML = '<strong>Đọc CCCD trên máy:</strong> SmartCar lấy dữ liệu từ mã QR mặt trước và dùng OCR chỉ cho vùng MRZ mặt sau để đối chiếu ngày sinh, giới tính và hạn thẻ. Không OCR toàn bộ CCCD và không đối soát với cơ sở dữ liệu nhà nước.';
        }

        const button = panel.querySelector('[data-ekyc-ocr]');
        if (button && !/Đang đọc/.test(button.textContent || '')) {
            button.textContent = 'Đọc QR + MRZ và tiếp tục';
        }

        const heading = panel.querySelector('[data-ekyc-step-pane="2"] .ekyc-pane-heading p');
        if (heading) {
            heading.textContent = 'Thông tin được lấy từ QR mặt trước và MRZ mặt sau. Hãy kiểm tra lại trước khi tiếp tục.';
        }
    };

    const install = () => {
        apply();
        window.setTimeout(apply, 400);
        window.setTimeout(apply, 1200);
        const panel = document.querySelector('[data-ekyc-panel="citizen"]');
        if (!panel) return;
        new MutationObserver(() => window.requestAnimationFrame(apply))
            .observe(panel, { childList: true, subtree: true });
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', install, { once: true });
    } else {
        install();
    }
})();
