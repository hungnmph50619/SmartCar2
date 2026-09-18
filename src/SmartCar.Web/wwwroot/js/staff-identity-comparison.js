(() => {
    document.addEventListener('DOMContentLoaded', () => {
        if (!/^\/Handovers\/Create/i.test(window.location.pathname)) return;

        const block = document.querySelector('[data-counter-citizen-evidence]');
        if (!(block instanceof HTMLElement)) return;

        const form = block.closest('form');
        const bookingId = Number(
            form?.querySelector('input[name="BookingId"]')?.value ||
            new URLSearchParams(window.location.search).get('bookingId'));
        if (!Number.isInteger(bookingId) || bookingId <= 0) return;

        const frontInput = block.querySelector('#counter-citizen-front');
        const backInput = block.querySelector('#counter-citizen-back');
        const status = block.querySelector('[data-counter-citizen-status]');
        if (!(frontInput instanceof HTMLInputElement) || !(backInput instanceof HTMLInputElement)) return;

        const comparison = document.createElement('div');
        comparison.className = 'staff-citizen-comparison mt-3';
        comparison.innerHTML = `
            <div class="small fw-semibold mb-2">Đối chiếu trực quan CCCD</div>
            <div class="row g-3">
                ${comparisonColumn('Hồ sơ KYC đã duyệt', 'kyc', bookingId)}
                ${comparisonColumn('Khách đang có mặt tại quầy', 'current', bookingId)}
            </div>
            <div class="form-text mt-2">Staff phải nhìn toàn bộ giấy tờ, số CCCD, ảnh chân dung và đặc điểm nhận dạng; ảnh chỉ hỗ trợ đối chiếu, không tự động kết luận danh tính.</div>`;

        const actionRow = block.querySelector('[data-counter-citizen-save]')?.closest('.d-flex');
        if (actionRow) actionRow.before(comparison);
        else block.appendChild(comparison);

        const currentFront = comparison.querySelector('[data-current-side="front"]');
        const currentBack = comparison.querySelector('[data-current-side="back"]');
        let frontObjectUrl = null;
        let backObjectUrl = null;

        const previewLocal = (input, img, side) => {
            const file = input.files?.[0];
            if (!(img instanceof HTMLImageElement)) return;

            if (side === 'front' && frontObjectUrl) URL.revokeObjectURL(frontObjectUrl);
            if (side === 'back' && backObjectUrl) URL.revokeObjectURL(backObjectUrl);

            if (!file) {
                refreshSecureImage(img, bookingId, side);
                return;
            }

            const url = URL.createObjectURL(file);
            if (side === 'front') frontObjectUrl = url;
            else backObjectUrl = url;
            img.src = url;
            img.classList.remove('opacity-50');
            img.nextElementSibling?.classList.add('d-none');
        };

        frontInput.addEventListener('change', () => previewLocal(frontInput, currentFront, 'front'));
        backInput.addEventListener('change', () => previewLocal(backInput, currentBack, 'back'));

        if (currentFront instanceof HTMLImageElement) refreshSecureImage(currentFront, bookingId, 'front');
        if (currentBack instanceof HTMLImageElement) refreshSecureImage(currentBack, bookingId, 'back');

        if (status) {
            const observer = new MutationObserver(() => {
                if (!/đã lưu an toàn/i.test(status.textContent || '')) return;
                if (currentFront instanceof HTMLImageElement && !frontInput.files?.length) {
                    refreshSecureImage(currentFront, bookingId, 'front');
                }
                if (currentBack instanceof HTMLImageElement && !backInput.files?.length) {
                    refreshSecureImage(currentBack, bookingId, 'back');
                }
            });
            observer.observe(status, { childList: true, characterData: true, subtree: true });
        }
    });

    function comparisonColumn(title, mode, bookingId) {
        const isKyc = mode === 'kyc';
        const frontSrc = isKyc
            ? `/StaffCounterIdentityEvidence/KycCitizen?bookingId=${bookingId}&side=front`
            : `/StaffCounterIdentityEvidence/CurrentHandoverCitizen?bookingId=${bookingId}&side=front`;
        const backSrc = isKyc
            ? `/StaffCounterIdentityEvidence/KycCitizen?bookingId=${bookingId}&side=back`
            : `/StaffCounterIdentityEvidence/CurrentHandoverCitizen?bookingId=${bookingId}&side=back`;
        const frontSideAttr = isKyc ? '' : ' data-current-side="front"';
        const backSideAttr = isKyc ? '' : ' data-current-side="back"';

        return `<div class="col-lg-6">
            <div class="border rounded-3 p-2 h-100 bg-body">
                <div class="fw-semibold small mb-2">${title}</div>
                <div class="row g-2">
                    <div class="col-sm-6">
                        <div class="small text-muted mb-1">Mặt trước</div>
                        <div class="staff-secure-doc-frame">
                            <img${frontSideAttr} src="${frontSrc}" alt="CCCD mặt trước - ${title}" loading="lazy" />
                            <span class="small text-muted staff-secure-doc-empty">Chưa có ảnh khả dụng</span>
                        </div>
                    </div>
                    <div class="col-sm-6">
                        <div class="small text-muted mb-1">Mặt sau</div>
                        <div class="staff-secure-doc-frame">
                            <img${backSideAttr} src="${backSrc}" alt="CCCD mặt sau - ${title}" loading="lazy" />
                            <span class="small text-muted staff-secure-doc-empty">Chưa có ảnh khả dụng</span>
                        </div>
                    </div>
                </div>
            </div>
        </div>`;
    }

    function refreshSecureImage(img, bookingId, side) {
        const url = `/StaffCounterIdentityEvidence/CurrentHandoverCitizen?bookingId=${bookingId}&side=${side}&v=${Date.now()}`;
        img.onerror = () => {
            img.classList.add('d-none');
            img.nextElementSibling?.classList.remove('d-none');
        };
        img.onload = () => {
            img.classList.remove('d-none');
            img.nextElementSibling?.classList.add('d-none');
        };
        img.src = url;
    }

    document.addEventListener('error', (event) => {
        const img = event.target;
        if (!(img instanceof HTMLImageElement) || !img.closest('.staff-secure-doc-frame')) return;
        img.classList.add('d-none');
        img.nextElementSibling?.classList.remove('d-none');
    }, true);

    document.addEventListener('load', (event) => {
        const img = event.target;
        if (!(img instanceof HTMLImageElement) || !img.closest('.staff-secure-doc-frame')) return;
        img.classList.remove('d-none');
        img.nextElementSibling?.classList.add('d-none');
    }, true);
})();
