document.addEventListener('DOMContentLoaded', () => {
    const timeInputIds = ['handover-time', 'return-time', 'requested-return-time', 'admin-requested-return-time'];

    timeInputIds.forEach(id => {
        const input = document.getElementById(id);
        if (!(input instanceof HTMLInputElement)) return;
        const currentValue = input.value;
        input.type = 'time';
        input.step = '60';
        input.removeAttribute('inputmode');
        input.removeAttribute('maxlength');
        input.removeAttribute('pattern');
        input.placeholder = '';
        if (currentValue) input.value = currentValue;
    });

    const fuelInputs = [
        document.getElementById('handover-fuel'),
        document.querySelector('form[action*="Returns"] input[name="FuelLevel"]')
    ].filter(input => input instanceof HTMLInputElement);

    fuelInputs.forEach(input => {
        input.type = 'number';
        input.min = '0';
        input.max = '100';
        input.step = '1';
        input.inputMode = 'numeric';

        const normalize = () => {
            const raw = String(input.value || '').replace(/[^0-9]/g, '').slice(0, 3);
            if (!raw) {
                input.value = '';
                input.setCustomValidity('');
                return;
            }

            const value = Math.min(100, Math.max(0, Number(raw)));
            input.value = String(value);
            input.setCustomValidity('');
        };

        input.addEventListener('input', normalize);
        input.addEventListener('change', normalize);
        input.addEventListener('paste', () => window.setTimeout(normalize, 0));
    });
});
