import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { LineupOptimizerComponent } from '../components/lineup-optimizer/lineup-optimizer.component';
import { MatchWorkbenchComponent } from '../components/match-workbench/match-workbench.component';

@NgModule({
  declarations: [LineupOptimizerComponent],
  imports: [CommonModule, FormsModule, TranslateModule, MatchWorkbenchComponent,
    RouterModule.forChild([{ path: '', component: LineupOptimizerComponent }])]
})
export class OptimizerPageModule {}
