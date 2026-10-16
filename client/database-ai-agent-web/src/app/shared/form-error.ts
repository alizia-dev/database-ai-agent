import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AbstractControl } from '@angular/forms';
import { MatFormFieldModule } from '@angular/material/form-field';

/** Renders the first validation message for a control (client-side rules or server field errors). */
@Component({
  selector: 'app-form-error',
  imports: [MatFormFieldModule],
  template: `{{ message() }}`,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FormError {
  readonly control = input.required<AbstractControl>();
  readonly label = input('This field');

  protected readonly message = computed(() => describe(this.control(), this.label()));
}

export function describe(control: AbstractControl, label: string): string {
  const errors = control.errors;
  if (!errors) {
    return '';
  }
  if (errors['server']) {
    return errors['server'] as string;
  }
  if (errors['required']) {
    return `${label} is required.`;
  }
  if (errors['email']) {
    return 'Enter a valid email address.';
  }
  if (errors['minlength']) {
    return `${label} must be at least ${(errors['minlength'] as { requiredLength: number }).requiredLength} characters.`;
  }
  if (errors['maxlength']) {
    return `${label} must be at most ${(errors['maxlength'] as { requiredLength: number }).requiredLength} characters.`;
  }
  if (errors['mismatch']) {
    return 'Passwords do not match.';
  }
  return `${label} is not valid.`;
}
