(() => {
    const normalize = value => (value ?? '').replace(/\s+/g, ' ').trim();
    const parseMoney = value => {
        const digits = (value ?? '').replace(/[^0-9-]/g, '');
        const amount = Number.parseInt(digits || '0', 10);
        return Number.isFinite(amount) ? amount : 0;
    };
    const money = value => new Intl.NumberFormat('vi-VN').format(Math.max(0, value)) + ' đ';

    const cardTitle = card => normalize(card?.querySelector(':scope > .card-header strong')?.textContent);
    const signedSelectorForTitle = title => title.includes('giao')
        ? 'a[href*="signed-handover-"]'
        : 'a[href*="signed-return-"]';
    const signedLinksForCard = card => Array.from(card.querySelectorAll(signedSelectorForTitle(cardTitle(card))));

    const refreshSignedContent = card => {
        const signedLinks = signedLinksForCard(card);

        signedLinks.forEach((link, index) => {
            link.className = 'btn btn-sm btn-outline-success text-decoration-none';
            link.textContent = `Trang ${index + 1}`;
        });

        card.querySelectorAll('strong').forEach(strong => {
            const text = normalize(strong.textContent);
            if (text === 'Bản ký biên bản giao:' || text === 'Bản ký biên bản trả:' || text.startsWith('Bản ký ')) {
                strong.textContent = signedLinks.length > 0 ? `Bản ký (${signedLinks.length} trang):` : 'Bản ký:';
            }
        });

        card.querySelectorAll('.col-6.col-md-4').forEach(column => {
            if (!column.querySelector('img')) return;
            column.querySelectorAll(':scope > .small.fw-semibold.mb-1').forEach(label => label.remove());
        });

        return signedLinks;
    };

    const rebuildRecordHeader = card => {
        const title = cardTitle(card);
        const header = card.querySelector(':scope > .card-header');
        const body = card.querySelector(':scope > .card-body.customer-record-details, :scope > .card-body:not(.customer-trip-record-summary)');
        if (!title || !header || !body) return;

        card.querySelectorAll(':scope > .card-body.customer-trip-record-summary').forEach(item => item.remove());

        const signedLinks = refreshSignedContent(card);
        const hasSigned = signedLinks.length > 0;
        body.classList.add('d-none', 'customer-record-details');

        header.innerHTML = '';
        header.className = 'card-header bg-white d-flex justify-content-between align-items-center gap-2 flex-wrap py-3';

        const heading = document.createElement('div');
        heading.className = 'd-flex align-items-center gap-2 flex-wrap';

        const headingText = document.createElement('strong');
        headingText.textContent = title;

        const badge = document.createElement('span');
        badge.className = hasSigned ? 'badge bg-success' : 'badge bg-secondary';
        badge.textContent = hasSigned ? 'Đã ký' : 'Chưa ký';

        heading.append(headingText, badge);

        const actions = document.createElement('div');
        actions.className = 'd-flex align-items-center gap-2 flex-wrap';

        const toggle = document.createElement('button');
        toggle.type = 'button';
        toggle.className = 'btn btn-sm btn-primary';
        toggle.textContent = 'Xem chi tiết';
        toggle.setAttribute('aria-expanded', 'false');
        toggle.addEventListener('click', () => {
            const opening = body.classList.contains('d-none');
            body.classList.toggle('d-none', !opening);
            toggle.textContent = opening ? 'Thu gọn' : 'Xem chi tiết';
            toggle.setAttribute('aria-expanded', opening ? 'true' : 'false');
        });

        actions.appendChild(toggle);
        header.append(heading, actions);
    };

    const initializeCustomerTripRecords = () => {
        if (!/^\/Bookings\/Details(?:\/|$)/i.test(window.location.pathname)) return;

        const completed = Array.from(document.querySelectorAll('.badge'))
            .some(badge => normalize(badge.textContent) === 'Đã hoàn tất');
        if (!completed) return;

        const recordTitles = new Set(['Biên bản giao xe', 'Biên bản trả xe']);
        const allCards = Array.from(document.querySelectorAll('.card'));
        const recordCards = allCards.filter(card => recordTitles.has(cardTitle(card)));
        const paymentCard = allCards.find(card => cardTitle(card) === 'Thanh toán');
        const refundCard = allCards.find(card => cardTitle(card) === 'Hoàn tiền');

        // Gom biên bản giao/trả thành một hồ sơ chuyến, thay vì để hai card rời rạc.
        if (recordCards.length > 0) {
            const wrapper = document.createElement('div');
            wrapper.className = 'card shadow-sm border-success mb-4';
            wrapper.innerHTML = `
                <div class="card-body">
                    <div class="d-flex justify-content-between align-items-center gap-3 flex-wrap">
                        <div>
                            <h2 class="h5 text-success mb-1">✓ Chuyến thuê đã hoàn tất</h2>
                            <div class="text-muted small">Biên bản giao, biên bản trả, ảnh đối chiếu và bản ký được lưu chung trong hồ sơ chuyến.</div>
                        </div>
                        <button type="button" class="btn btn-success" data-customer-trip-record-toggle>Ẩn hồ sơ chuyến thuê</button>
                    </div>
                    <div class="mt-3" data-customer-trip-record-content></div>
                </div>`;

            recordCards[0].before(wrapper);
            const content = wrapper.querySelector('[data-customer-trip-record-content]');
            recordCards.forEach(card => {
                card.classList.remove('mb-4');
                card.classList.add('mb-3');
                content?.appendChild(card);
            });

            const toggle = wrapper.querySelector('[data-customer-trip-record-toggle]');
            toggle?.addEventListener('click', () => {
                const closing = content?.classList.contains('d-none') !== true;
                content?.classList.toggle('d-none', closing);
                toggle.textContent = closing ? 'Xem hồ sơ chuyến thuê' : 'Ẩn hồ sơ chuyến thuê';
            });
        }

        // Mỗi biên bản chỉ có một nút Xem chi tiết ở header. Không lặp dòng tóm tắt km/ngày/nhiên liệu.
        recordCards.forEach(rebuildRecordHeader);

        // Đổi nhãn các khoản hoàn cọc để khách hiểu nguồn hoàn tiền.
        if (refundCard) {
            const refundRows = Array.from(refundCard.querySelectorAll('.card-body > .d-flex'));
            const depositRows = refundRows.filter(row => normalize(row.querySelector('strong')?.textContent) === 'Hoàn cọc');
            depositRows.forEach((row, index) => {
                const label = row.querySelector('strong');
                if (!label) return;
                label.textContent = index === depositRows.length - 1
                    ? 'Hoàn cọc sau khi kết thúc chuyến'
                    : 'Hoàn phần cọc điều chỉnh';
            });

            const heading = refundCard.querySelector(':scope > .card-header strong');
            if (heading) heading.textContent = 'Chi tiết hoàn tiền';
        }

        // Lấy tổng đã thanh toán từ hóa đơn - đây là dòng tiền khách đã thực chuyển trong cả chuyến.
        const paidRow = Array.from(document.querySelectorAll('tfoot tr'))
            .find(row => normalize(row.textContent).includes('Đã xác nhận thanh toán'));
        const paidTotal = parseMoney(paidRow?.querySelector('td:last-child')?.textContent);

        // Chỉ cộng các khoản đã hoàn thực tế, không cộng khoản còn đang chờ hoàn.
        let refundedTotal = 0;
        if (refundCard) {
            const refundRows = Array.from(refundCard.querySelectorAll('.card-body > .d-flex'));
            refundRows.forEach(row => {
                const status = normalize(row.querySelector('.small.text-muted')?.textContent);
                if (!status.includes('Đã hoàn')) return;
                const amountCandidates = Array.from(row.querySelectorAll('div'))
                    .map(element => normalize(element.textContent))
                    .filter(text => /\d[\d.,]*\s*đ/.test(text));
                if (amountCandidates.length > 0) refundedTotal += parseMoney(amountCandidates[0]);
            });
        }

        const netPaid = Math.max(0, paidTotal - refundedTotal);

        // Card quyết toán cuối chuyến: hiển thị kết quả trước, lịch sử giao dịch xem sau.
        if (paymentCard) {
            const settlement = document.createElement('div');
            settlement.className = 'card shadow-sm border-success mb-4';
            settlement.innerHTML = `
                <div class="card-header bg-white d-flex justify-content-between align-items-center gap-2 flex-wrap">
                    <div>
                        <strong>Quyết toán chuyến thuê</strong>
                        <div class="small text-muted">Kết quả tài chính cuối cùng sau tất cả khoản thu và hoàn tiền.</div>
                    </div>
                    <span class="badge bg-success">Đã quyết toán</span>
                </div>
                <div class="card-body">
                    <div class="d-flex justify-content-between py-2 border-bottom"><span>Tổng đã thanh toán</span><strong>${money(paidTotal)}</strong></div>
                    <div class="d-flex justify-content-between py-2 border-bottom"><span>Tổng đã hoàn lại</span><strong>${money(refundedTotal)}</strong></div>
                    <div class="d-flex justify-content-between align-items-center py-3"><strong>Chi phí thực tế chuyến thuê</strong><strong class="text-primary fs-4">${money(netPaid)}</strong></div>
                    <div class="small text-muted mb-3">Chi phí thực tế = tổng tiền khách đã thanh toán − tổng tiền SmartCar đã hoàn.</div>
                    <button type="button" class="btn btn-outline-primary w-100" data-customer-payment-history-toggle>Xem chi tiết giao dịch</button>
                </div>`;
            paymentCard.before(settlement);

            paymentCard.classList.add('d-none');
            const paymentHeading = paymentCard.querySelector(':scope > .card-header strong');
            if (paymentHeading) paymentHeading.textContent = 'Lịch sử thanh toán';
            refundCard?.classList.add('d-none');

            const historyToggle = settlement.querySelector('[data-customer-payment-history-toggle]');
            historyToggle?.addEventListener('click', () => {
                const opening = paymentCard.classList.contains('d-none');
                paymentCard.classList.toggle('d-none', !opening);
                refundCard?.classList.toggle('d-none', !opening);
                historyToggle.textContent = opening ? 'Ẩn chi tiết giao dịch' : 'Xem chi tiết giao dịch';
            });
        }
    };

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initializeCustomerTripRecords);
    else initializeCustomerTripRecords();
})();
