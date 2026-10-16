import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatListModule } from '@angular/material/list';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth/auth.service';
import {
  DatabaseConnection,
  DatabaseService,
  Paged,
  ProcedureDetail,
  ProcedureSummary,
  SchemaOverview,
  TableDetail,
  TableSummary,
  behaviorBadges,
} from '../../core/database/database.service';
import { toApiProblem } from '../../core/http/api-problem';

const POLL_MS = 3_000;

@Component({
  selector: 'app-schema-page',
  imports: [
    DatePipe, FormsModule, RouterLink, MatCardModule, MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule,
    MatSelectModule, MatTabsModule, MatListModule, MatProgressBarModule,
  ],
  templateUrl: './schema-page.html',
  styleUrl: './schema-page.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SchemaPage implements OnInit {
  private readonly database = inject(DatabaseService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly snackBar = inject(MatSnackBar);
  protected readonly auth = inject(AuthService);

  protected readonly connections = signal<DatabaseConnection[]>([]);
  protected readonly connectionId = signal<string | null>(null);
  protected readonly overview = signal<SchemaOverview | null>(null);
  protected readonly tables = signal<Paged<TableSummary> | null>(null);
  protected readonly procedures = signal<Paged<ProcedureSummary> | null>(null);
  protected readonly selectedTable = signal<TableDetail | null>(null);
  protected readonly selectedProcedure = signal<ProcedureDetail | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected schemaFilter = '';
  protected tableSearch = '';
  protected procedureSearch = '';
  protected includeMissing = false;

  protected readonly badges = behaviorBadges;
  protected readonly canAnalyze = computed(() => this.auth.hasPermission('database.analyze'));
  protected readonly analysis = computed(() => this.overview()?.latestAnalysis ?? null);
  protected readonly analysisActive = computed(() => ['Queued', 'Running'].includes(this.analysis()?.status ?? ''));

  private pollTimer: ReturnType<typeof setTimeout> | undefined;

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.pollTimer));
  }

  async ngOnInit(): Promise<void> {
    try {
      const connections = await this.database.list();
      this.connections.set(connections);
      const requested = this.route.snapshot.queryParamMap.get('connection');
      const initial = connections.find((c) => c.id === requested)?.id ?? connections[0]?.id ?? null;
      if (initial) {
        await this.select(initial);
      }
    } catch (error) {
      this.error.set(toApiProblem(error).message);
    } finally {
      this.loading.set(false);
    }
  }

  async select(connectionId: string): Promise<void> {
    this.connectionId.set(connectionId);
    this.selectedTable.set(null);
    this.selectedProcedure.set(null);
    void this.router.navigate([], { queryParams: { connection: connectionId }, replaceUrl: true });
    await this.refresh();
  }

  async analyze(): Promise<void> {
    const id = this.connectionId();
    if (!id) {
      return;
    }
    try {
      await this.database.analyze(id);
      this.snackBar.open('Analysis started. Only metadata is read; this can take a minute for large databases.', 'Dismiss', { duration: 5000 });
      await this.refresh();
    } catch (error) {
      this.snackBar.open(toApiProblem(error).message, 'Dismiss', { duration: 6000 });
    }
  }

  async searchTables(page = 1): Promise<void> {
    const id = this.connectionId();
    if (id) {
      this.tables.set(await this.database.tables(id, { schema: this.schemaFilter, search: this.tableSearch, page }));
    }
  }

  async searchProcedures(page = 1): Promise<void> {
    const id = this.connectionId();
    if (id) {
      this.procedures.set(await this.database.procedures(id, { schema: this.schemaFilter, search: this.procedureSearch, page, includeMissing: this.includeMissing }));
    }
  }

  async openTable(table: TableSummary): Promise<void> {
    this.selectedTable.set(await this.database.table(this.connectionId()!, table.id));
  }

  async openProcedure(procedure: ProcedureSummary): Promise<void> {
    this.selectedProcedure.set(await this.database.procedure(this.connectionId()!, procedure.id));
  }

  protected pages(result: Paged<unknown> | null): number {
    return result ? Math.max(1, Math.ceil(result.total / result.pageSize)) : 1;
  }

  private async refresh(): Promise<void> {
    const id = this.connectionId();
    if (!id) {
      return;
    }
    clearTimeout(this.pollTimer);
    try {
      this.overview.set(await this.database.schema(id));
      await Promise.all([this.searchTables(), this.searchProcedures()]);
    } catch (error) {
      this.error.set(toApiProblem(error).message);
    }
    if (this.analysisActive()) {
      this.pollTimer = setTimeout(() => void this.refresh(), POLL_MS);
    }
  }
}
