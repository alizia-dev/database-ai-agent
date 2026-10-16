import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterOutlet } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { SystemInfoService } from '../../core/system/system-info.service';

type ApiStatus = { state: 'checking' } | { state: 'online'; version: string } | { state: 'offline' };

@Component({
  selector: 'app-public-shell',
  imports: [RouterOutlet, RouterLink, MatToolbarModule],
  templateUrl: './public-shell.html',
  styleUrl: './public-shell.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PublicShell {
  protected readonly year = new Date().getFullYear();

  protected readonly apiStatus = toSignal(
    inject(SystemInfoService)
      .getInfo()
      .pipe(
        map((info): ApiStatus => ({ state: 'online', version: info.version })),
        catchError(() => of<ApiStatus>({ state: 'offline' })),
      ),
    { initialValue: { state: 'checking' } as ApiStatus },
  );
}
