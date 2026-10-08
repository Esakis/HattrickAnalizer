import { CommonModule } from '@angular/common';
import { NgModule } from '@angular/core';
import { RouterModule } from '@angular/router';
import { TranslateModule } from '@ngx-translate/core';
import { TrainingViewComponent } from '../components/training-view/training-view.component';

@NgModule({
  declarations: [TrainingViewComponent],
  imports: [CommonModule, TranslateModule, RouterModule.forChild([{ path: '', component: TrainingViewComponent }])]
})
export class TrainingPageModule {}
