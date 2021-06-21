import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ShoppingListDropdownComponent } from './shopping-list-dropdown.component';

describe('ShoppingListDropdownComponent', () => {
  let component: ShoppingListDropdownComponent;
  let fixture: ComponentFixture<ShoppingListDropdownComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [ ShoppingListDropdownComponent ]
    })
    .compileComponents();
  });

  beforeEach(() => {
    fixture = TestBed.createComponent(ShoppingListDropdownComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
