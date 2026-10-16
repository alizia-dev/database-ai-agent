import { AbstractControl, ValidationErrors, ValidatorFn, Validators } from '@angular/forms';

/** Mirrors the server policy (length over composition). The server remains the authority. */
export const PASSWORD_MIN_LENGTH = 12;
export const PASSWORD_MAX_LENGTH = 128;

export const passwordValidators: ValidatorFn[] = [
  Validators.required,
  Validators.minLength(PASSWORD_MIN_LENGTH),
  Validators.maxLength(PASSWORD_MAX_LENGTH),
];

export const PASSWORD_HINT = `At least ${PASSWORD_MIN_LENGTH} characters. A short phrase is easier to remember and harder to guess.`;

/** Group validator: `confirm` must equal `password`. */
export function matchPasswords(passwordKey: string, confirmKey: string): ValidatorFn {
  return (group: AbstractControl): ValidationErrors | null => {
    const confirm = group.get(confirmKey);
    if (!confirm) {
      return null;
    }
    const mismatch = group.get(passwordKey)?.value !== confirm.value;
    const { mismatch: _, ...others } = confirm.errors ?? {};
    confirm.setErrors(mismatch && confirm.value ? { ...others, mismatch: true } : Object.keys(others).length ? others : null);
    return null;
  };
}
